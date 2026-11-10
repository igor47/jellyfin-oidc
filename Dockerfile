# syntax=docker/dockerfile:1.7
#
# jellyfin-oidc image.
#
# Build context: a checkout of a patch branch (oidc/vX.Y), NOT this repo.
#
#   docker buildx build -f /path/to/main/Dockerfile \
#     --build-arg UPSTREAM_VERSION=12.4 --build-arg FORK_VERSION=12.4-oidc.1 \
#     -t jellyfin-oidc:dev --load /path/to/patch-checkout
#
# How it works: the server is compiled with the same `dotnet publish` flags as
# upstream's jellyfin-packaging (docker/Dockerfile), then copied over the
# server files in the official jellyfin/jellyfin image of the same version.
# Everything else in the official image is kept as is: jellyfin-ffmpeg, the
# GPU userspace drivers, fonts, jemalloc, the entrypoint, the healthcheck and
# the stock jellyfin-web under /jellyfin/jellyfin-web.
#
# The final stage only uses COPY, so no emulation is needed to produce a
# linux/arm64 image on an amd64 builder: dotnet cross-publishes for the
# target RID natively on the build platform.

ARG UPSTREAM_VERSION
# Optional digest pin for the base image, e.g. "@sha256:...". Empty = tag only.
ARG BASE_DIGEST=""
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0

#
# Source + restore, on the build platform.
#
FROM --platform=$BUILDPLATFORM ${DOTNET_SDK_IMAGE} AS source
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /src
COPY . .
# Refuse to build something that is not a Jellyfin server tree.
RUN test -f Jellyfin.Server/Jellyfin.Server.csproj && test -f SharedVersion.cs

#
# Publish the server (upstream flags: --self-contained, no debug symbols).
#
FROM source AS publish
ARG TARGETARCH
RUN --mount=type=cache,id=jellyfin-oidc-nuget,target=/root/.nuget/packages \
    case "${TARGETARCH}" in \
      amd64) DOTNET_ARCH=x64 ;; \
      arm64) DOTNET_ARCH=arm64 ;; \
      *) echo "unsupported TARGETARCH ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
 && dotnet publish Jellyfin.Server --arch "${DOTNET_ARCH}" --configuration Release \
      --output=/server --self-contained \
      -p:DebugSymbols=false -p:DebugType=none

#
# Unit tests: `docker buildx build --target test ...`. Not part of the image.
# TEST_PROJECTS: space separated paths to test .csproj files.
# TEST_FILTER: a `dotnet test --filter` expression (empty = all tests).
#
FROM source AS test
ARG TEST_PROJECTS=""
ARG TEST_FILTER=""
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
# `dotnet test` exits 0 when a filter matches nothing; treat that as failure.
RUN --mount=type=cache,id=jellyfin-oidc-nuget,target=/root/.nuget/packages \
    set -e; \
    if [ -z "${TEST_PROJECTS}" ]; then echo "TEST_PROJECTS is empty" >&2; exit 1; fi; \
    for p in ${TEST_PROJECTS}; do \
      echo "::: dotnet test ${p} filter='${TEST_FILTER}'"; \
      args=(--configuration Release --logger "console;verbosity=normal"); \
      if [ -n "${TEST_FILTER}" ]; then args+=(--filter "${TEST_FILTER}"); fi; \
      dotnet test "${p}" "${args[@]}" 2>&1 | tee /tmp/test.log; \
      if grep -q "No test matches" /tmp/test.log || ! grep -qE "Total tests: [1-9]" /tmp/test.log; then \
        echo "::: no tests ran for ${p} (filter '${TEST_FILTER}')" >&2; exit 1; \
      fi; \
    done

#
# Final image: official upstream image with the server files replaced.
#
FROM jellyfin/jellyfin:${UPSTREAM_VERSION}${BASE_DIGEST} AS final
ARG UPSTREAM_VERSION
ARG FORK_VERSION
ARG SOURCE_REPO=https://github.com/igor47/jellyfin-oidc
ARG SOURCE_REVISION=unknown

# The published server is overlaid file by file onto /jellyfin. Files are
# overwritten, not deleted first (deleting would need a RUN, i.e. emulation
# for arm64). The server only loads assemblies listed in jellyfin.deps.json,
# which is replaced too, so a leftover upstream file cannot be picked up.
# jellyfin-web is not touched.
COPY --from=publish /server/ /jellyfin/

# Identify the fork without touching the version clients see (12.x.0 stays
# what SharedVersion.cs says; clients gate features on it).
ENV JELLYFIN_OIDC_FORK_VERSION=${FORK_VERSION}
LABEL org.opencontainers.image.title="jellyfin-oidc" \
      org.opencontainers.image.description="Unofficial build of Jellyfin Server with built-in OpenID Connect login. Not affiliated with the Jellyfin project." \
      org.opencontainers.image.version="${FORK_VERSION}" \
      org.opencontainers.image.source="${SOURCE_REPO}" \
      org.opencontainers.image.url="${SOURCE_REPO}" \
      org.opencontainers.image.documentation="${SOURCE_REPO}#readme" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.licenses="GPL-2.0-only" \
      org.opencontainers.image.base.name="docker.io/jellyfin/jellyfin:${UPSTREAM_VERSION}" \
      org.opencontainers.image.vendor="igor47 (unofficial)" \
      io.github.igor47.jellyfin-oidc.upstream-version="${UPSTREAM_VERSION}" \
      io.github.igor47.jellyfin-oidc.fork-version="${FORK_VERSION}" \
      io.github.igor47.jellyfin-oidc.source-tag="${SOURCE_REPO}/tree/v${FORK_VERSION}" \
      io.github.igor47.jellyfin-oidc.upstream-source="https://github.com/jellyfin/jellyfin/tree/v${UPSTREAM_VERSION}"
