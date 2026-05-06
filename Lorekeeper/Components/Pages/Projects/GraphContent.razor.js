let visLoadPromise;

function loadScript(src) {
  return new Promise((resolve, reject) => {
    const existing = document.querySelector(`script[src="${src}"]`);
    if (existing) {
      if (existing.dataset.loaded === "true") resolve();
      else existing.addEventListener("load", () => resolve(), { once: true });
      return;
    }

    const script = document.createElement("script");
    script.src = src;
    script.async = true;
    script.onload = () => {
      script.dataset.loaded = "true";
      resolve();
    };
    script.onerror = () => reject(new Error(`Failed to load ${src}`));
    document.head.appendChild(script);
  });
}

function loadStylesheet(href) {
  if (document.querySelector(`link[href="${href}"]`)) return;
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = href;
  document.head.appendChild(link);
}

async function ensureVis() {
  if (window.vis?.Network && window.vis?.DataSet) return window.vis;
  visLoadPromise ??= (async () => {
    loadStylesheet("/lib/vis-network/vis-network.min.css");
    await loadScript("/lib/vis-network/vis-network.min.js");
    if (!window.vis?.Network || !window.vis?.DataSet) {
      throw new Error("vis-network loaded but did not expose window.vis.Network.");
    }
    return window.vis;
  })();
  return visLoadPromise;
}

export async function attach(container, dotNetRef) {
  const vis = await ensureVis();
  const nodes = new vis.DataSet([]);
  const edges = new vis.DataSet([]);
  let disposed = false;

  const network = new vis.Network(container, { nodes, edges }, {
    autoResize: true,
    interaction: {
      hover: true,
      keyboard: { enabled: true, bindToWindow: false },
      multiselect: false,
      navigationButtons: false,
      tooltipDelay: 120,
    },
    layout: {
      improvedLayout: true,
    },
    physics: {
      enabled: true,
      solver: "forceAtlas2Based",
      forceAtlas2Based: {
        gravitationalConstant: -42,
        centralGravity: 0.015,
        springLength: 132,
        springConstant: 0.06,
        damping: 0.62,
        avoidOverlap: 0.45,
      },
      stabilization: {
        enabled: true,
        iterations: 180,
        fit: true,
      },
    },
    nodes: {
      borderWidth: 1.5,
      borderWidthSelected: 3,
      shadow: {
        enabled: true,
        color: "rgba(0, 0, 0, 0.45)",
        size: 10,
        x: 0,
        y: 2,
      },
      margin: 8,
      scaling: {
        min: 10,
        max: 32,
      },
      font: {
        face: "Helvetica Neue, Arial, sans-serif",
        strokeWidth: 3,
        strokeColor: "rgba(16, 18, 22, 0.85)",
      },
    },
    edges: {
      width: 1,
      selectionWidth: 2,
      hoverWidth: 1.6,
      smooth: {
        enabled: true,
        type: "dynamic",
        roundness: 0.38,
      },
    },
  });

  network.on("click", (params) => {
    if (disposed) return;
    const nodeId = params.nodes?.length ? String(params.nodes[0]) : null;
    dotNetRef.invokeMethodAsync("OnGraphNodeSelected", nodeId);
  });

  network.on("doubleClick", (params) => {
    if (!params.nodes?.length) return;
    network.focus(params.nodes[0], { scale: 1.25, animation: { duration: 320, easingFunction: "easeInOutQuad" } });
  });

  return {
    render(nextNodes, nextEdges, options) {
      nodes.update(nextNodes ?? []);
      edges.update(nextEdges ?? []);

      const nextNodeIds = new Set((nextNodes ?? []).map((node) => String(node.id)));
      const nextEdgeIds = new Set((nextEdges ?? []).map((edge) => String(edge.id)));
      const staleNodeIds = nodes.getIds().filter((id) => !nextNodeIds.has(String(id)));
      const staleEdgeIds = edges.getIds().filter((id) => !nextEdgeIds.has(String(id)));
      if (staleEdgeIds.length) edges.remove(staleEdgeIds);
      if (staleNodeIds.length) nodes.remove(staleNodeIds);

      network.setOptions({ physics: { enabled: !!options?.physics } });
      if (options?.selectedNodeId && nextNodeIds.has(String(options.selectedNodeId))) {
        network.selectNodes([String(options.selectedNodeId)]);
      } else {
        network.unselectAll();
      }

      if ((nextNodes?.length ?? 0) > 0 && !network.getScale()) {
        network.fit({ animation: false });
      }
    },
    fit() {
      network.fit({ animation: { duration: 280, easingFunction: "easeInOutQuad" } });
    },
    selectNode(nodeId) {
      if (!nodeId) {
        network.unselectAll();
        return;
      }
      if (!nodes.get(String(nodeId))) return;
      network.selectNodes([String(nodeId)]);
      network.focus(String(nodeId), { scale: 1.18, animation: { duration: 260, easingFunction: "easeInOutQuad" } });
    },
    setPhysics(enabled) {
      network.setOptions({ physics: { enabled: !!enabled } });
    },
    dispose() {
      disposed = true;
      network.destroy();
    },
  };
}