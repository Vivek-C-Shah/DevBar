# DevBar website

Static marketing site plus the legal pages Google's OAuth consent screen requires. No build step, no framework, no dependencies to install - three HTML files, one stylesheet, one script, and the product's own screenshots.

```
website/
  index.html          landing page
  privacy.html        privacy policy  → /privacy
  terms.html          terms of service → /terms
  vercel.json         clean URLs + cache/security headers
  assets/css/site.css tokens copied from ../.tastemaker/style-lock.md
  assets/js/site.js    GSAP + ScrollTrigger choreography
  assets/img/          logo, product screenshots, Lucide icons
```

## Run it locally

```bash
cd website
python -m http.server 8080
# then open http://localhost:8080
```

`/privacy` and `/terms` need Vercel's `cleanUrls` to work without the `.html`; locally, use `privacy.html` and `terms.html`.

## Deploy to Vercel

```bash
npm i -g vercel        # if you don't have it
cd website
vercel login           # one-time, opens the browser
vercel --prod
```

There is no framework to detect and nothing to build - accept the defaults (Other / no build command / output `.`). The first deploy prints the production URL; use it everywhere below.

To put it on a custom domain, add the domain in the Vercel dashboard, then update the four `https://devbar-neon.vercel.app` strings in `index.html`, `privacy.html` and `terms.html` (canonical + Open Graph URLs).

## The Google OAuth consent screen

Google rejected the earlier GitHub links because the home page domain wasn't verifiably yours and a `blob/…/PRIVACY.md` page isn't a real privacy page. With this site deployed:

| Field on the Branding page | Value |
|---|---|
| App name | Vivek's Jarvis |
| Support email | your Gmail |
| App home page | `https://<your-deploy>.vercel.app/` |
| Privacy policy | `https://<your-deploy>.vercel.app/privacy` |
| Terms of service | `https://<your-deploy>.vercel.app/terms` |
| Authorized domain | `vercel.app`, or your custom domain |

**Verify ownership** so the home-page check passes:

1. Open [Google Search Console](https://search.google.com/search-console) and add a **URL prefix** property for your deployed URL.
2. Choose the **HTML tag** method and copy the `<meta name="google-site-verification" …>` tag.
3. Paste it into `index.html` - there's a commented placeholder in `<head>` marked for exactly this - then redeploy and click Verify.

A custom domain you actually own (e.g. `devbar.yourdomain.dev`) is the smoothest path; Google is fussier about shared suffixes like `vercel.app`.

## Keeping it honest

Numbers on the landing page are measured, not marketing: 0.0% idle CPU and ~130 MB working set come from sampling the running process, and the Jarvis latencies come from the trace log (~500 ms speech end-detection, ~400 ms to the model's first token, ~350 ms to Aura's first audio once warm). If the product changes, update the copy - a landing page that overstates the thing is worse than no landing page.

## Credits

- **Icons** - [Lucide](https://lucide.dev) (ISC) fetched via Iconify; no attribution required, credited here as a courtesy.
- **Screenshots** - captured from DevBar running on Windows 11. Nothing is mocked up; the hero "bar" is rebuilt in DOM so it can animate like the real one.
- **Fonts** - Inter and JetBrains Mono via Google Fonts, chosen as the web equivalents of the app's Segoe UI Variable / Cascadia Mono pairing.
