# Regenerating the README GIF

`demo-frames.html` is the hero interaction as discrete frames: `setFrame(n)`
sets the shade height, the screenshot, the cursor and the caption, with no CSS
transitions, so a headless capture produces the same frames every run.

It expects the module screenshots next to it in `shots/` — copy them from
`website/assets/img/shots/`.

To rebuild: load the file, call `window.setFrame(i)` for `i` in
`0..window.FRAME_COUNT-1`, screenshot `#stage` each time, then assemble at
120ms per frame. The current GIF is 55 frames, 680px wide, 128 colours, ~1.2MB.

Edit the storyboard at the bottom of the file to change the beats. Keep it under
about 2MB: GitHub will serve a larger one, but it is the first thing on the page
and a slow first screen is the thing the GIF exists to avoid.
