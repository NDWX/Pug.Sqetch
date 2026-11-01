# The sqetch-deploy container image: the self-contained Linux release archive on the .NET
# runtime-deps base, which supplies what the single file needs from the OS (ICU, OpenSSL) and a shell
# for CI systems that run job scripts inside the image.
#
# Built by Installers/build/Image.ps1 from a context it stages, where rootfs-<arch>/ is the extracted
# release archive for each architecture. There is no RUN step, so every architecture builds on any
# host without emulation, and the image holds exactly the files the archive does.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble

ARG TARGETARCH

LABEL org.opencontainers.image.title="sqetch-deploy" \
      org.opencontainers.image.description="Bundle Sqetch projects and deploy the bundles to a database" \
      org.opencontainers.image.source="https://github.com/NDWX/Pug.Sqetch" \
      org.opencontainers.image.licenses="GPL-3.0-or-later AND AGPL-3.0-or-later"

# the whole folder: the native SQLite library has to stay beside the executable
COPY rootfs-${TARGETARCH}/ /opt/sqetch-deploy/

ENV PATH="/opt/sqetch-deploy:${PATH}"

WORKDIR /work

# the base image's non-root 'app' user (1654); mount a writable folder, or pass --user, for anything
# the tool writes: a bundle, a SQLite database
USER $APP_UID

ENTRYPOINT ["sqetch-deploy"]
