# Deployment build traceability

CI passes `github.sha` into the API assembly and both Docker builds. Production
containers never inspect Git and do not require a `.git` directory.

The API exposes its full compiled SHA at `GET /api/health` as `buildSha`.
VirtualDisplay exposes its full SHA at `GET /build-info.json` and shows the
seven-character form in its footer.

For an image deployed by SHA tag, these values must all be identical:

```bash
EXPECTED_SHA=$(git rev-parse HEAD)
IMAGE=ghcr.io/OWNER/IMAGE:$EXPECTED_SHA

test "$(docker image inspect "$IMAGE" --format '{{index .Config.Labels "org.opencontainers.image.revision"}}')" = "$EXPECTED_SHA"
```

API:

```bash
test "$(curl -fsS https://API_HOST/api/health | jq -r .buildSha)" = "$EXPECTED_SHA"
```

VirtualDisplay:

```bash
test "$(curl -fsS https://DISPLAY_HOST/build-info.json | jq -r .buildSha)" = "$EXPECTED_SHA"
```

Also inspect the running container's configured immutable image reference:

```bash
docker inspect CONTAINER --format '{{.Config.Image}}'
```
