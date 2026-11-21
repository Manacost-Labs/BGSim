# BGSim.Server: the combat simulator as an HTTP API (docs/server.md). A Native AOT binary on a chiseled base image:
# no shell, no package manager, non-root user (the CI job prints the image size and memory).
#
#   docker build -t bgsim-server --build-arg GIT_SHA=$(git rev-parse HEAD) .
#   docker run --rm -p 127.0.0.1:8080:8080 --read-only --cap-drop ALL bgsim-server
#
# Built natively for the machine's architecture (linux/amd64 or linux/arm64): Native AOT does not cross-compile
# between CPU architectures here.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
ARG TARGETARCH
WORKDIR /src

# Restore from the project files alone, so that a change in the code does not download the packages again.
COPY src/BGSim/BGSim.csproj src/BGSim/
COPY src/BGSim.Server/BGSim.Server.csproj src/BGSim.Server/
RUN case "${TARGETARCH:-amd64}" in \
        amd64) echo linux-x64 ;; \
        arm64) echo linux-arm64 ;; \
        *) echo "unsupported TARGETARCH ${TARGETARCH}" >&2; exit 1 ;; \
    esac > /tmp/rid \
    && dotnet restore src/BGSim.Server/BGSim.Server.csproj -r "$(cat /tmp/rid)"

COPY src/BGSim/ src/BGSim/
COPY src/BGSim.Server/ src/BGSim.Server/
# The commit /v1/info reports (the build context has no .git).
ARG GIT_SHA=unknown
RUN dotnet publish src/BGSim.Server/BGSim.Server.csproj -c Release -r "$(cat /tmp/rid)" --no-restore -o /app \
        -p:SourceRevisionId="${GIT_SHA}" -p:EnableSourceControlManagerQueries=false

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app/BGSim.Server /app/BGSim.Server
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
# No curl in a chiseled image: the binary checks itself (GET /health on localhost, exit 0 or 1).
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --start-interval=1s --retries=3 CMD ["/app/BGSim.Server", "--healthcheck"]
ENTRYPOINT ["/app/BGSim.Server"]
