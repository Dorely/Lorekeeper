# Lorekeeper on Kubernetes

Self-hosted, single-user server deployment. The pod serves the same browser
application as local hosting, locked behind the single-user login, with the
SQLite database and session keys on a persistent volume.

Constraints to respect:

- **Exactly one replica.** SQLite and the application's process-wide write lease
  require a single process. The Deployment uses `Recreate` so an upgrade never
  runs two pods against the same volume; do not scale above one.
- **Plain HTTP inside the cluster.** The container disables the HTTPS redirect
  and issues session cookies that work over HTTP. Expose it only on a trusted
  network (`kubectl port-forward`, NodePort on a LAN) or terminate TLS in front
  of it.
- **The volume is the backup boundary.** `/data` holds the database, its
  protected migration backups, and Data Protection keys. Snapshot the
  PersistentVolume to back up the whole workspace.

## 1. Build and publish the image

From the repository root:

```bash
docker build -t lorekeeper:0.3.9 .
```

Push it to a registry your cluster can pull from (or import it directly, e.g.
`k3s ctr images import` / `kind load docker-image`), then set that reference as
the `image:` in `deployment.yaml`.

## 2. Generate login credentials

```bash
docker run --rm lorekeeper:0.3.9 auth-setup \
  --username <name> --password '<a long password>'
```

Copy `secret.example.yaml` to `secret.yaml` (it is not committed), paste the
three printed values into it, and add the printed `otpauth://` URI to your
authenticator app. The plaintext password is never stored anywhere.

## 3. Deploy

```bash
kubectl apply -f namespace.yaml
kubectl apply -f pvc.yaml -f secret.yaml -f deployment.yaml -f service.yaml
kubectl -n lorekeeper rollout status deployment/lorekeeper
```

Apply the files individually as shown. Applying the whole directory would also
apply `secret.example.yaml`, which shares the Secret's name and would overwrite
your real credentials with placeholders.

First startup runs database migrations before the pod reports ready; the
startup probe allows several minutes for that.

## 4. Open it

The Service is a NodePort pinned to `30455`, so the app answers on every node's
LAN address. Browse to `http://<any-node-ip>:30455` and sign in with your
username, password, and current authenticator code. Switch the Service type to
ClusterIP if you prefer access through `kubectl port-forward` only.

## Upgrades

Build and push a new image tag, update `deployment.yaml`, and re-apply. The pod
restarts, migrates the database under its protected backup contract, and
reports ready when the workspace is current. Startup migration failures keep
the original database and its protected backup on the volume.
