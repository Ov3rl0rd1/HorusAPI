# Tiny backup sidecar: postgres client + age + rclone on Alpine. Idle footprint is a few
# MB (it mostly sleeps); pg_dump/age/rclone are short-lived streams.
FROM alpine:3.20

# postgresql16-client matches the postgres:16-alpine server; age = encryption; rclone = upload.
RUN apk add --no-cache postgresql16-client age rclone ca-certificates tzdata

COPY backup.sh     /usr/local/bin/backup.sh
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/backup.sh /usr/local/bin/entrypoint.sh

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
