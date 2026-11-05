# Sample workspace

This folder is a ready-to-use TermRest workspace:

| File | Purpose |
|---|---|
| `TermRest Demo.postman_collection.json` | Requests against [Postman Echo](https://postman-echo.com): query parameters, JSON and form bodies, a path variable, Basic auth, bearer auth inherited from a folder, and a client-certificate (mTLS) request |
| `Postman Echo.postman_environment.json` | Sets `{{baseUrl}}` and `{{token}}` |
| `.termrest/settings.json` | Selects the *Postman Echo* environment |
| `.termrest/certificates.json` | Binds the badssl.com client certificate to `https://client.badssl.com/*` |

Both JSON files are ordinary Postman exports, so you can import them into Postman unchanged.

```sh
termrest samples                     # open the UI
termrest run "samples/TermRest Demo.postman_collection.json" -e "Postman Echo"
```

## Trying mutual TLS with badssl.com

[badssl.com](https://badssl.com/) publishes a test client certificate, and `https://client.badssl.com/` only answers 200 when that certificate is presented.

```sh
mkdir -p samples/certs
curl -o samples/certs/badssl.com-client.p12 https://badssl.com/certs/badssl.com-client.p12
export BADSSL_PFX_PASSPHRASE=badssl.com          # PowerShell: $env:BADSSL_PFX_PASSPHRASE = 'badssl.com'

termrest certs list samples                       # ✓ CN=BadSSL Client Certificate … key ok
termrest send "samples/TermRest Demo.postman_collection.json" "badssl.com client certificate" -v
```

With the certificate the response is `200 OK`, and the TLS details report "Client cert asked: yes" and "Client cert sent: CN=BadSSL Client Certificate". Without it, the server answers `400 No required SSL certificate was sent`. `samples/certs/` is git-ignored.
