# ============================================================
# Mediasoup SFU Server (Node.js)
# ============================================================
FROM node:20-bookworm AS build

WORKDIR /app

# Install build dependencies for mediasoup
RUN apt-get update && apt-get install -y \
    python3 \
    make \
    g++ \
    && rm -rf /var/lib/apt/lists/*

# Copy package files
COPY src/mediasoup/package.json ./
RUN npm install

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
