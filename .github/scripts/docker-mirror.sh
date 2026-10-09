#!/usr/bin/env bash
# The GitHub-hosted runners share addresses, and Docker Hub counts the anonymous pulls per address: the images of the tests and of the end-to-end stack
# (postgres, redis, rabbitmq, seaweedfs) then fail with "toomanyrequests". The daemon asks a public mirror first and falls back to Docker Hub.
set -euo pipefail
sudo mkdir -p /etc/docker
[ -f /etc/docker/daemon.json ] || echo '{}' | sudo tee /etc/docker/daemon.json > /dev/null
sudo jq '. + {"registry-mirrors": ["https://mirror.gcr.io"]}' /etc/docker/daemon.json | sudo tee /tmp/daemon.json > /dev/null
sudo mv /tmp/daemon.json /etc/docker/daemon.json
sudo systemctl restart docker
docker info --format '{{.RegistryConfig.Mirrors}}'
