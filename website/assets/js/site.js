/* DevBar site behaviour. No dependencies.
   Motion copies the product: the card unrolls from the idle pill, hide is faster than
   show, nothing bounces. Every animation here has a job: the hero demonstrates the one
   interaction the product is built on, the tabs switch state, the rail shows sequence. */

(() => {
  const reduce = window.matchMedia('(prefers-reduced-motion: reduce)');

  // ---------- theme ----------
  // The <head> script has already set the theme before paint; this is only the switch.
  const root = document.documentElement;
  const themeBtn = document.querySelector('[data-theme-toggle]');
  const themeMeta = document.querySelector('meta[name="theme-color"]');
  const osLight = window.matchMedia('(prefers-color-scheme: light)');
  const readChoice = () => {
    try { return localStorage.getItem('devbar-theme'); } catch (e) { return null; }
  };
  const paintTheme = () => {
    const light = root.dataset.theme === 'light';
    if (themeMeta) themeMeta.content = light ? '#f5f5f6' : '#111113';
    if (themeBtn) {
      themeBtn.setAttribute('aria-label', light ? 'Switch to the dark theme' : 'Switch to the light theme');
    }
  };
  const setTheme = (light) => {
    if (light) root.dataset.theme = 'light';
    else delete root.dataset.theme;
    paintTheme();
  };
  paintTheme();

  if (themeBtn) {
    themeBtn.addEventListener('click', () => {
      const light = root.dataset.theme !== 'light';
      setTheme(light);
      try { localStorage.setItem('devbar-theme', light ? 'light' : 'dark'); } catch (e) { /* private mode */ }
    });
  }

  // Keep following the OS, but only until the reader has picked for themselves.
  osLight.addEventListener('change', (e) => {
    if (!readChoice()) setTheme(e.matches);
  });

  // ---------- nav border once the page has scrolled (no scroll listener) ----------
  const nav = document.querySelector('.nav');
  const sentinel = document.querySelector('.nav-sentinel');
  if (nav && sentinel) {
    new IntersectionObserver(([e]) => nav.classList.toggle('is-stuck', !e.isIntersecting)).observe(sentinel);
  }

  // ---------- legal pages: highlight the section you're reading ----------
  const tocLinks = [...document.querySelectorAll('.toc a')];
  if (tocLinks.length) {
    const spy = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          tocLinks.forEach((a) =>
            a.classList.toggle('is-current', a.getAttribute('href') === `#${entry.target.id}`)
          );
        });
      },
      { rootMargin: '-80px 0px -70% 0px' }
    );
    tocLinks
      .map((a) => document.querySelector(a.getAttribute('href')))
      .filter(Boolean)
      .forEach((h) => spy.observe(h));
  }

  // ---------- hero: the bar, docked to the stage's top edge ----------
  const stage = document.querySelector('[data-stage]');
  if (stage) {
    const bar = stage.querySelector('.bar');
    const shots = [...stage.querySelectorAll('.bar-card img')];
    const finePointer = window.matchMedia('(hover: hover) and (pointer: fine)').matches;
    let index = 0;
    let timer = 0;
    let hovering = false;
    let visible = true;

    const hint = stage.querySelector('.stage-hint');
    if (hint && !finePointer) hint.textContent = 'Tap the pill to open or close it.';

    const setOpen = (open) => {
      bar.classList.toggle('is-open', open);
      stage.classList.toggle('is-open', open);
      bar.setAttribute('aria-expanded', String(open));
    };
    const show = (i) => shots.forEach((img, n) => img.classList.toggle('is-on', n === i));

    // Idle, open on a module, hold, close, next module. Paused while you hover
    // (you're in control then), off-screen, or in a background tab.
    const HOLD_CLOSED = 1600;
    const HOLD_OPEN = 3600;
    // one pending timer at a time, whoever schedules it
    const schedule = (fn, ms) => {
      clearTimeout(timer);
      timer = setTimeout(fn, ms);
    };
    const next = () => {
      index = (index + 1) % shots.length;
      show(index);
      step();
    };
    const step = () => {
      clearTimeout(timer);
      if (hovering || !visible || document.hidden) return;
      if (bar.classList.contains('is-open')) {
        setOpen(false);
        schedule(next, HOLD_CLOSED);
      } else {
        setOpen(true);
        schedule(step, HOLD_OPEN);
      }
    };

    if (reduce.matches) {
      setOpen(true);
    } else {
      // the observer fires once on load, which starts the loop
      new IntersectionObserver(([e]) => {
        visible = e.isIntersecting;
        if (visible) schedule(step, 700);
        else clearTimeout(timer);
      }).observe(stage);
      document.addEventListener('visibilitychange', () => {
        if (document.hidden) clearTimeout(timer);
        else if (visible) schedule(step, 500);
      });
    }

    if (finePointer) {
      let leaveTimer = 0;
      stage.addEventListener('pointerenter', () => {
        clearTimeout(timer);
        clearTimeout(leaveTimer);
        hovering = true;
        setOpen(true);
      });
      stage.addEventListener('pointerleave', () => {
        hovering = false;
        // the app waits a beat before collapsing, so a wobble off the edge doesn't close it
        leaveTimer = setTimeout(() => {
          if (reduce.matches) return;
          setOpen(false);
          schedule(next, HOLD_CLOSED);
        }, 300);
      });
    }

    // Touch and keyboard: the bar is a button, so a tap or Enter toggles it.
    bar.addEventListener('click', () => {
      if (finePointer && hovering) return; // hover already opened it
      clearTimeout(timer);
      setOpen(!bar.classList.contains('is-open'));
    });
  }

  // ---------- modules: a tab strip, like the bar's own ----------
  const viewer = document.querySelector('[data-viewer]');
  if (viewer) {
    const tabs = [...viewer.querySelectorAll('[role="tab"]')];
    const frame = viewer.querySelector('.viewer-frame');
    const shots = [...frame.querySelectorAll('img')];
    const desc = viewer.querySelector('.viewer-desc');

    const select = (tab, focus) => {
      tabs.forEach((t) => {
        const on = t === tab;
        t.setAttribute('aria-selected', String(on));
        t.tabIndex = on ? 0 : -1;
      });
      const n = Number(tab.dataset.shot);
      shots.forEach((img, i) => img.classList.toggle('is-on', i === n));
      frame.setAttribute('aria-labelledby', tab.id);
      if (desc) desc.textContent = tab.querySelector('.desc').textContent;
      if (focus) {
        tab.focus();
        tab.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: reduce.matches ? 'auto' : 'smooth' });
      }
    };

    tabs.forEach((tab, i) => {
      tab.addEventListener('click', () => select(tab, false));
      tab.addEventListener('keydown', (e) => {
        const next = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[e.key];
        if (next) {
          e.preventDefault();
          select(tabs[(i + next + tabs.length) % tabs.length], true);
        } else if (e.key === 'Home' || e.key === 'End') {
          e.preventDefault();
          select(tabs[e.key === 'Home' ? 0 : tabs.length - 1], true);
        }
      });
    });
  }

  // ---------- Jarvis rail: the loop lights up in order as it scrolls in ----------
  const rail = document.querySelector('[data-rail]');
  if (rail) {
    const legs = [...rail.querySelectorAll('.leg')];
    const light = () => {
      legs.forEach((leg, i) => {
        setTimeout(() => {
          leg.classList.add('is-lit');
          rail.style.setProperty('--lit', String(i / (legs.length - 1)));
        }, reduce.matches ? 0 : i * 220);
      });
    };
    const io = new IntersectionObserver(([e]) => {
      if (!e.isIntersecting) return;
      io.disconnect();
      light();
    }, { rootMargin: '0px 0px -25% 0px' });
    io.observe(rail);
  }

  // ---------- entrances ----------
  // Visible by default. Only elements still below the fold get hidden, and only just
  // before they arrive, so nothing is ever blank for a crawler or a screenshot.
  if (reduce.matches || !('IntersectionObserver' in window)) return;

  const targets = [];
  document.querySelectorAll('[data-reveal]').forEach((el) => targets.push([el, 0]));
  document.querySelectorAll('[data-reveal-group]').forEach((group) =>
    [...group.children].forEach((el, i) => targets.push([el, i]))
  );

  // `near` hides an element while it is still just below the viewport; `arrive` plays
  // the entrance once it is actually on screen.
  const arrive = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        const el = entry.target;
        arrive.unobserve(el);
        // two frames: commit the hidden state, then transition out of it
        requestAnimationFrame(() => requestAnimationFrame(() => el.classList.add('revealed')));
      });
    },
    { rootMargin: '0px 0px -8% 0px' }
  );
  const near = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        const el = entry.target;
        near.unobserve(el);
        el.classList.add('will-reveal');
        arrive.observe(el);
      });
    },
    { rootMargin: '0px 0px 35% 0px' }
  );

  const fold = window.innerHeight;
  targets.forEach(([el, i]) => {
    if (el.getBoundingClientRect().top < fold * 1.35) return; // on or near screen at load: leave it be
    el.style.setProperty('--d', String(i));
    near.observe(el);
  });
})();
