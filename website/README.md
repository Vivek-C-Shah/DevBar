# DevBar website

Static marketing site plus the legal pages Google's OAuth consent screen requires. No build step, no framework, no dependencies, no third-party requests - three HTML files, one stylesheet, one script, and the product's own screenshots.

```
website/
  index.html          landing page
  privacy.html        privacy policy  → /privacy
  terms.html          terms of service → /terms
  assets/css/site.css tokens copied from ../docs/style-lock.md
  assets/js/site.js    hero bar demo, module tabs, reveals (vanilla, no libraries)
  assets/img/          logo, product screenshots, Phosphor icons
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

## Themes

Dark is the default, because the product is dark and so is every screenshot here. The
page follows `prefers-color-scheme` unless the reader clicks the toggle in the nav,
which stores a choice in `localStorage` under `devbar-theme`; the inline script in
`<head>` applies it before first paint so there is no flash. With JavaScript off the
page stays dark, which is the designed default rather than a fallback.

Both palettes live in `site.css` as two blocks of custom properties, `:root` and
`:root[data-theme="light"]`. Nothing outside those blocks names a colour, so a new
theme is one more block and no other edits. The light values are picked against the
light floor rather than derived from the dark ones: the blue and the green are
darkened because `#5ea1ff` on paper is 2.2:1. Every text pair clears WCAG AA; the
tightest is a link on a well band at 4.7:1.

The exception is `--shot-bg` and `--shot-line`, which stay dark in both themes. They
are the surface under a product screenshot and the mock bar chrome in the hero, and a
light frame around a dark screenshot would imply a light mode the app does not have.

## Keeping it honest

Numbers on the landing page are measured, not marketing: 0.0% idle CPU and ~130 MB working set come from sampling the running process, and the Jarvis latencies come from the trace log (~500 ms speech end-detection, ~400 ms to the model's first token, ~350 ms to Aura's first audio once warm). If the product changes, update the copy - a landing page that overstates the thing is worse than no landing page.

## Credits

- **Icons** - [Phosphor](https://phosphoricons.com) (MIT), tinted in CSS through a mask so they follow text colour, which is also how the theme toggle swaps its sun for a moon.
- **Screenshots** - captured from DevBar running on Windows 11. Nothing is mocked up; the hero unrolls the real screenshots through a clip-path so it moves like the bar does.
- **Fonts** - none downloaded. Segoe UI Variable and Cascadia Mono, both already on the Windows machines this tool runs on, with system-ui and ui-monospace behind them.

## Deploy config lives at the repo root

`../vercel.json`, not here. The project deploys from the repository root with
`outputDirectory: "website"`, so a `vercel.json` in this folder is never read. There
used to be one, it drifted from the real one, and editing it did nothing; it is gone
now. Rewrites, cache headers and security headers all belong in the root file.

## Cache busting

`vercel.json` serves `/assets/img/*` as immutable for a year, because an image never
changes under its own name. `site.css` and `site.js` do change under theirs, so they
are served `max-age=0, must-revalidate` and revalidate on every load: a 304 when
nothing moved, the new file when something did.

They used to be immutable too, with a `?v=` query on the tags to bust it. That does
not work, and the way it fails is worth knowing about. **Vercel's edge ignores the
query string when it keys its cache**, so `site.css?v=4` and `site.css?v=8` both
return whatever is currently deployed. A browser holding the previous HTML therefore
asks for the old `?v=` and is handed the *new* stylesheet. The page then runs old
markup against new CSS, which is how the theme toggle once rendered as two grey
rectangles: the HTML still had the mask-icon spans, the CSS no longer had the rule
that gave them a mask image, and an unmasked `.i` paints its `background:
currentColor` as a solid box.

The `?v=` is still on the tags as a second line of defence, since it does give the
browser a genuinely new cache key. It is not what makes this correct.
