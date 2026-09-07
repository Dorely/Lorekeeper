"use strict";

const Electron = require("electron");
const { Connector } = require("./connector");

const MAX_INCHES = 100;
const MIN_INCHES = 0.5;
const STANDARD_PAPERS = [
    ["Letter", 8.5, 11],
    ["Legal", 8.5, 14],
    ["A4", 210 / 25.4, 297 / 25.4],
    ["A3", 297 / 25.4, 420 / 25.4]
];
const LOOPBACK_HOSTS = new Set(["localhost", "127.0.0.1"]);
const activePrints = new Map();
const resultJson = value => JSON.stringify(value);

class HookService extends Connector {
    onHostReady() { }

    constructor(socket, app) {
        super(socket, app);
        this.app = app;
        this.on("lorekeeper-print", (serializedRequest, done) => {
            this.print(serializedRequest, done);
        });
        this.on("lorekeeper-print-cancel", (jobId, done) => {
            const active = activePrints.get(String(jobId));
            if (active) {
                active.cancelRequested = true;
                if (!active.window.isDestroyed()) active.window.destroy();
            }
            done(resultJson({ status: "requested" }));
        });
        app.on("will-quit", () => {
            for (const active of activePrints.values()) {
                if (!active.window.isDestroyed()) active.window.destroy();
            }
            activePrints.clear();
        });
    }

    async print(serializedRequest, done) {
        let window;
        let active;
        try {
            const request = JSON.parse(serializedRequest);
            const url = new URL(request.documentUrl);
            if (url.protocol !== "http:" || !LOOPBACK_HOSTS.has(url.hostname)
                || !/^\/projects\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/print-sessions\/[0-9a-f]{32}\/document$/i.test(url.pathname))
                throw new Error("The print URL is outside the allowed local project route.");
            const applicationWindow = Electron.BrowserWindow.getAllWindows().find(candidate => {
                try { return new URL(candidate.webContents.getURL()).origin === url.origin && ![...activePrints.values()].some(item => item.window === candidate); }
                catch { return false; }
            });
            if (!applicationWindow || url.search || url.hash || url.username || url.password)
                throw new Error("The print document must belong to the running application.");
            if (!/^[0-9a-f]{32}$/i.test(request.jobId) || activePrints.has(request.jobId))
                throw new Error("The print operation is already active or invalid.");
            const width = Number(request.widthInches);
            const height = Number(request.heightInches);
            if (!Number.isFinite(width) || !Number.isFinite(height)
                || width < MIN_INCHES || width > MAX_INCHES
                || height < MIN_INCHES || height > MAX_INCHES)
                throw new Error("The print dimensions are outside the supported range.");
            const paper = standardPaper(width, height);
            if (!paper) throw new Error("The print paper size is unsupported.");

            window = new Electron.BrowserWindow({
                parent: applicationWindow,
                modal: true,
                show: false,
                width: 800,
                height: 600,
                title: String(request.title || "Lorekeeper"),
                autoHideMenuBar: true,
                skipTaskbar: true,
                webPreferences: {
                    nodeIntegration: false,
                    contextIsolation: true,
                    sandbox: true,
                    webSecurity: true,
                    devTools: false
                }
            });
            active = { window, cancelRequested: false };
            activePrints.set(String(request.jobId), active);
            window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
            window.webContents.on("will-navigate", (event, destination) => {
                if (destination !== url.href) event.preventDefault();
            });

            await window.loadURL(url.href);
            if (active.cancelRequested) return done(resultJson({ status: "canceled" }));
            await waitForPrintReady(window.webContents);
            if (active.cancelRequested) return done(resultJson({ status: "canceled" }));
            // Windows needs a visible owner for its modal printer dialog.
            window.show();
            window.focus();
            const result = await printWithCallback(window, {
                silent: false,
                printBackground: true,
                margins: { marginType: "none" },
                header: "",
                footer: "",
                pageSize: paper.name,
                landscape: paper.landscape
            });
            done(resultJson(active.cancelRequested ? { status: "canceled" } : result));
        } catch (error) {
            done(resultJson(active?.cancelRequested ? { status: "canceled" } : { status: "failed", error: safeError(error) }));
        } finally {
            if (serializedRequest) {
                try { activePrints.delete(String(JSON.parse(serializedRequest).jobId)); } catch { }
            }
            if (window && !window.isDestroyed())
                window.destroy();
        }
    }
}

function standardPaper(width, height) {
    for (const [name, paperWidth, paperHeight] of STANDARD_PAPERS) {
        if (Math.abs(width - paperWidth) < 0.01 && Math.abs(height - paperHeight) < 0.01)
            return { name, landscape: false };
        if (Math.abs(width - paperHeight) < 0.01 && Math.abs(height - paperWidth) < 0.01)
            return { name, landscape: true };
    }
    return null;
}

function waitForPrintReady(webContents) {
    return new Promise((resolve, reject) => {
        let settled = false;
        let timer;
        const finish = (callback, value) => {
            if (settled) return;
            settled = true;
            clearTimeout(timeout);
            clearTimeout(timer);
            callback(value);
        };
        const timeout = setTimeout(() => finish(reject, new Error("The print document did not become ready.")), 30000);
        const check = async () => {
            try {
                const ready = await webContents.executeJavaScript("(async()=>{if(!window.lorekeeperPrintReady)throw new Error('Print document unavailable');await window.lorekeeperPrintReady;if(window.lorekeeperPrintError)throw new Error(window.lorekeeperPrintError);return true})()", false);
                if (settled) return;
                if (ready) finish(resolve); else timer = setTimeout(check, 50);
            } catch (error) {
                finish(reject, error);
            }
        };
        check();
    });
}

function printWithCallback(window, options) {
    return new Promise(resolve => {
        let settled = false;
        const finish = result => {
            if (settled) return;
            settled = true;
            window.removeListener("closed", onClosed);
            resolve(result);
        };
        const onClosed = () => finish({ status: "canceled", error: "Print window closed" });
        window.once("closed", onClosed);
        const webContents = window.webContents;
        webContents.print(options, (success, failureReason) => {
            finish(success
                ? { status: "success" }
                : { status: /print job cancel(?:ed|led)/i.test(failureReason || "") ? "canceled" : "failed", error: failureReason || "Print job failed" });
        });
    });
}

function safeError(error) {
    return error && typeof error.message === "string" ? error.message : "The system print operation failed.";
}

module.exports = { HookService };
