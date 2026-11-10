# Playwright runner for the e2e suite (Chromium only is used).
FROM mcr.microsoft.com/playwright:v1.65.0-noble
WORKDIR /e2e
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY playwright.config.ts summarize.mjs ./
COPY tests ./tests
ENTRYPOINT []
CMD ["npx", "playwright", "test"]
