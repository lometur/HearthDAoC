#!/usr/bin/env bash
# Healthy when the game port accepts TCP connections.
ip="${HEARTHDAOC_LISTEN_IP:-0.0.0.0}"
[[ "$ip" == 0.0.0.0 ]] && ip=127.0.0.1
exec bash -c "</dev/tcp/$ip/${HEARTHDAOC_PORT:-10301}" 2>/dev/null
