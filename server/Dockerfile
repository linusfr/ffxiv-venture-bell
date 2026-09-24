FROM golang:1.26-alpine AS build
WORKDIR /src
# No dependencies beyond the standard library, so there is nothing to download
# and the module cache layer would only be noise.
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /venturebell .
# Created here so it can be copied in with an owner. A bare VOLUME /data would
# be made by the daemon at run time, owned by root, and the nonroot user could
# not write the state file into it.
RUN mkdir /data

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /venturebell /venturebell
COPY --from=build --chown=65532:65532 /data /data
# The binary defaults to localhost, which inside a container means unreachable.
ENV BELL_ADDR=0.0.0.0:8770 \
    BELL_STATE=/data/state.json
EXPOSE 8770
VOLUME /data
USER nonroot:nonroot
ENTRYPOINT ["/venturebell"]
