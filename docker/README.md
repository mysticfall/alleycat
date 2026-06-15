# Docker Services

## Purpose

This directory hosts local service repositories for the Docker services needed to run the AlleyCat platform.
Run all commands below from this `docker/` directory.

## Prerequisites

- Git is installed.
- Docker and Docker Compose are installed and running.

## Setup

Clone the required repositories into the current directory:

```sh
git clone https://github.com/mysticfall/audio2face-api-server.git
git clone https://github.com/devnen/Chatterbox-TTS-Server.git
```

Start the services:

```sh
docker-compose up
```
