# The sqetch-deploy container image

Every `sqetch-deploy` release is an image for linux/amd64 and linux/arm64:
`ghcr.io/ndwx/sqetch-deploy:<v>`, also on Docker Hub as `ndwx/sqetch-deploy:<v>`. Releases are also
tagged `latest`; pin a version anyway.

```sh
docker run --rm -v "$PWD:/work" --user "$(id -u):$(id -g)" ghcr.io/ndwx/sqetch-deploy:0.1.0 \
  deploy app.zip --driver postgres --postgres-connection-string "$CONNECTION_STRING"
```

- The entrypoint is `sqetch-deploy`, so the arguments are its own.
- The working directory is `/work`. Mount the folder holding the bundle, or the project to bundle,
  there.
- It runs as a non-root user (`app`, uid 1654). Pass `--user` with your own ids when it must write
  into the mounted folder (a bundle, a SQLite database), so it can, and the files stay yours. A
  deployment to a database server writes nothing locally and needs no `--user`.
- A database on the host is not `localhost` inside the container. Use the host's address, or
  `--network host` on Linux.

Bundling through the image:

```sh
docker run --rm -v "$PWD:/work" --user "$(id -u):$(id -g)" ghcr.io/ndwx/sqetch-deploy:0.1.0 \
  bundle --project db -o app.zip
```

In CI systems that start a shell inside the image (GitLab CI), override the entrypoint; see
`sqetch-ci`'s `references/gitlab-ci.md`.
