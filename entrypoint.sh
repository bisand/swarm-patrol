#!/bin/sh
set -e

# Optionally load env vars
[ -f /app/.env ] && export $(grep -v '^#' /app/.env | xargs)

# Print out env vars from .env, scrambling secret values
if [ -f /app/.env ]; then
  echo "Loaded environment variables from .env (secrets scrambled):"
  while IFS= read -r line; do
    # Skip comments and empty lines
    case "$line" in
      ''|\#*) continue ;;
    esac
    varname="${line%%=*}"
    varvalue="${line#*=}"
    # Scramble if variable name contains sensitive keywords
    if echo "$varname" | grep -Eiq '(SECRET|KEY|TOKEN|PASSWORD)'; then
      echo "$varname=****"
    else
      echo "$varname=$varvalue"
    fi
  done < /app/.env
fi

# Optional debug log
echo "Starting Nuxt app with CMD: $@"

# Execute the CMD (e.g. node /app/server/index.mjs)
exec "$@"
