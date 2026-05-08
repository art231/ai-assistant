# ============================================================
# Stage 1: Build Angular app
# ============================================================
FROM node:20-alpine AS build
WORKDIR /app

# Copy package files
COPY src/frontend/package.json ./
RUN npm install

# Copy source files
COPY src/frontend/ ./

# Build Angular app
RUN npm run build

# ============================================================
# Stage 2: Serve with nginx
# ============================================================
FROM nginx:alpine AS runtime

# Copy nginx config
COPY docker/frontend/nginx.conf /etc/nginx/nginx.conf

# Copy built Angular app
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD wget -qO- http://localhost:80/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
