# swarm-patrol

## Overview

**swarm-patrol** is a .NET-based automation tool for Docker Swarm environments. It continuously monitors your Swarm services (or standalone containers) that use images from GitHub Container Registry (GHCR) and ensures they are always running the latest image digest. If a new image is available, swarm-patrol will update the service to use the new digest, ensuring your deployments are always up to date.

### Key Features
- **Automatic polling** of Docker Swarm services or standalone containers for GHCR images.
- **Digest comparison**: Detects if a service/container is running an outdated image digest.
- **Automated service update**: Updates Swarm services to the latest digest if needed.
- **Fallback logic**: If a digest update fails, falls back to updating by tag only.
- **Configurable polling interval**.
- **Secure authentication** to GHCR using a GitHub Personal Access Token (PAT).
- **Detailed logging** for all actions and errors.

---

## How It Works

1. **Startup**: On launch, swarm-patrol checks for required environment variables and logs into GHCR using the Docker CLI (if available).
2. **Polling Loop**: It determines if Docker is running in Swarm mode. If so, it polls all Swarm services; otherwise, it polls standalone containers.
3. **Image Check**: For each relevant service/container, it parses the image reference, fetches the latest digest from GHCR, and compares it to the running digest.
4. **Update**: If a new digest is found, it pulls the new image and updates the service. If the update fails, it falls back to updating by tag.
5. **Repeat**: The process repeats at the configured interval.

---

## Requirements

- Docker (host or Swarm manager node)
- .NET 9 runtime (if running manually)
- Access to `/var/run/docker.sock` (for Docker API access)
- GitHub Personal Access Token (PAT) with `read:packages` scope for private GHCR images

---

## Environment Variables

| Variable         | Description                                      | Required | Example                |
|------------------|--------------------------------------------------|----------|------------------------|
| `GHCR_OWNER`     | GHCR org/user (e.g. `valueretail`)               | Yes      | `valueretail`          |
| `GHCR_USERNAME`  | GitHub username for GHCR login                   | Yes      | `bisand`               |
| `GHCR_TOKEN`     | GitHub PAT for GHCR authentication               | Yes      | `ghp_...`              |
| `POLL_INTERVAL`  | Polling interval in seconds (default: 300)       | No       | `60`                   |

---

## Usage

### 1. Manual Run (Standalone)

#### Build and Run Locally

```sh
# Build the project
DOTNET_ENV=Production dotnet publish -c Release -o out

# Run with required environment variables
export GHCR_OWNER=valueretail
export GHCR_USERNAME=bisand
export GHCR_TOKEN=ghp_...
export POLL_INTERVAL=60

# Ensure Docker socket is available
sudo docker run -v /var/run/docker.sock:/var/run/docker.sock \
  -e GHCR_OWNER \
  -e GHCR_USERNAME \
  -e GHCR_TOKEN \
  -e POLL_INTERVAL=60 \
  ghcr.io/bisand/swarm-patrol:latest
```

Or use an `.env` file and pass it with `--env-file`:

```sh
docker run --env-file .env -v /var/run/docker.sock:/var/run/docker.sock ghcr.io/bisand/swarm-patrol:latest
```

### 2. Docker Compose

```yaml
version: "3.8"
services:
  swarm-patrol:
    image: ghcr.io/bisand/swarm-patrol:latest
    environment:
      GHCR_OWNER: ${GHCR_OWNER}
      GHCR_USERNAME: ${GHCR_USERNAME}
      GHCR_TOKEN: ${GHCR_TOKEN}
      POLL_INTERVAL: ${POLL_INTERVAL}
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

### 3. Docker Swarm Deployment

```yaml
version: "3.8"
services:
  swarm-patrol:
    image: ghcr.io/bisand/swarm-patrol:latest
    environment:
      GHCR_OWNER: ${GHCR_OWNER}
      GHCR_USERNAME: ${GHCR_USERNAME}
      GHCR_TOKEN: ${GHCR_TOKEN}
      POLL_INTERVAL: ${POLL_INTERVAL}
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    deploy:
      placement:
        constraints:
          - node.role == manager
      restart_policy:
        condition: on-failure
```

#### Using Docker Secrets for Environment Variables
If you want to use Docker secrets for sensitive values, mount the secret as a file and use a script or entrypoint to load it into the environment before starting the app.

---

## Security Notes
- **Never commit your `.env` file or secrets to version control.**
- Use Docker secrets or environment variables for sensitive data in production.
- The GitHub PAT should have the minimum required scope (`read:packages`).

---

## Troubleshooting
- **`exec format error`**: Ensure your image matches your host architecture (e.g., both amd64/x86_64 or both arm64).
- **Environment variables not set**: Use `--env-file` or `-e` flags with `docker run`, or ensure your orchestrator passes them.
- **Docker socket errors**: Make sure `/var/run/docker.sock` is mounted and accessible.
- **GHCR authentication errors**: Double-check your PAT, username, and owner values.

---

## License

This project is licensed under the MIT License.