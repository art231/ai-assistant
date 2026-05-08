# ============================================================
# Mediasoup SFU Server (Node.js)
# ============================================================
FROM node:20-bookworm AS build

WORKDIR /app

# Disable SSL verification for environments with TLS issues (e.g., corporate proxies)
ENV NODE_TLS_REJECT_UNAUTHORIZED=0
ENV PYTHONHTTPSVERIFY=0
ENV npm_config_strict_ssl=false

# Install build dependencies for mediasoup
RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    python3-venv \
    make \
    g++ \
    pkg-config \
    libssl-dev \
    ca-certificates \
    openssl \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Copy package files
COPY src/mediasoup/package.json ./
RUN npm install

# Fix clock skew issue in Docker (files extracted from archives may have timestamps in the future)
RUN find /app -exec touch {} \;

# Pre-download OpenSSL source for mediasoup worker build (meson wrap)
# This avoids TLS download failures during npm install postinstall
RUN mkdir -p /app/node_modules/mediasoup/worker/subprojects/packagecache && \
    curl -fsSL -o /app/node_modules/mediasoup/worker/subprojects/packagecache/openssl-3.0.8.tar.gz \
    https://github.com/openssl/openssl/releases/download/openssl-3.0.8/openssl-3.0.8.tar.gz || \
    curl -fsSL -o /app/node_modules/mediasoup/worker/subprojects/packagecache/openssl-3.0.8.tar.gz \
    https://github.com/mesonbuild/wrapdb/releases/download/openssl_3.0.8-3/openssl-3.0.8.tar.gz || \
    echo "WARNING: Could not pre-download OpenSSL, will try during build"

# Re-run npm install to trigger mediasoup postinstall (which builds the worker)
# The pre-downloaded OpenSSL should now be found in the meson cache
RUN cd /app && npm install

# Fix clock skew again after second npm install
RUN find /app -exec touch {} \;

# Copy source files
COPY src/mediasoup/ ./

# Build TypeScript
RUN npm run build || true

# ============================================================
# Runtime stage
# ============================================================
FROM node:20-bookworm-slim

WORKDIR /app

# Install runtime dependencies for mediasoup
RUN apt-get update && apt-get install -y \
    python3 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY --from=build /app/package.json ./

EXPOSE 3000
EXPOSE 40000-49999/udp

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD node -e "require('http').get('http://localhost:3000/health', r => {process.exit(r.statusCode === 200 ? 0 : 1)}).on('error', () => process.exit(1))"

CMD ["node", "dist/index.js"]
