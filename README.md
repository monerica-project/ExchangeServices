# ExchangeServices

A shared .NET library of cryptocurrency exchange price/rate sources used across the
Monerica network (for example [MoneroPriceNow](https://moneropricenow.com) and SwapRaven).
Each exchange has its own client that fetches quotes; new price sources self-register via
configuration so consumers can add or remove exchanges without code changes.

- `ExchangeServices/` — the library (per-exchange clients, options, registration).
- `ExchangeServices.Console/` — a small console harness for exercising the sources.

Per-exchange verbose logging is gated behind the `EXCHANGE_VERBOSE` environment variable
(off in production).

## License

MIT — see the [`LICENSE`](LICENSE) file.
