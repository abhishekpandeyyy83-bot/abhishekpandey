# Morrow storefront

## Run locally

Install the .NET 8 SDK, then run:

```powershell
dotnet run
```

Open the local URL printed by ASP.NET Core. The server serves the storefront and its API from the same origin. Order and newsletter records are saved to `data/shop.json`; set `MORROW_DATA_FILE` to use another path.

## Deploy on Render

Create a new Blueprint from this repository in Render. The included `render.yaml` deploys the .NET app as a free web service. Render provides a public `onrender.com` URL after the first build. Free services can sleep when idle, and their local filesystem is ephemeral; use a persistent database and an appropriate paid plan before collecting real customer orders.

## API

- `GET /api/products` returns the catalog and server-side prices in cents.
- `POST /api/orders` accepts `{ "email": "you@example.com", "items": [{ "productId": "sunday-tote", "quantity": 1 }] }`. The server validates products and computes the total. This demo records orders but does not process payments.
- `POST /api/newsletter` accepts `{ "email": "you@example.com" }`.
- `GET /healthz` is a health check.

For deployment, use an ASP.NET Core host and configure persistent storage for `MORROW_DATA_FILE`. GitHub Pages can host static files only; it cannot run this backend. No payment provider or production order-management system is configured.
