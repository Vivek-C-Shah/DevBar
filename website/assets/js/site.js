/* DevBar site motion.
   Choreography deliberately copies the product's own: the card "unrolls" from the
   idle pill (height first, no overshoot), blobs drift slowly out of sync, nothing
   bounces. Everything is wrapped in gsap.matchMedia so reduced-motion users get a
   finished, static page instead of a broken one. */

(() => {
  const nav = document.querySelector('.nav');
  if (nav) {
    const onScroll = () => nav.classList.toggle('is-stuck', window.scrollY > 8);
    onScroll();
    window.addEventListener('scroll', onScroll, { passive: true });
  }

  // Legal pages: highlight the section you're reading.
  const tocLinks = [...document.querySelectorAll('.toc a')];
  if (tocLinks.length) {
    const headings = tocLinks
      .map((a) => document.querySelector(a.getAttribute('href')))
      .filter(Boolean);
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
    headings.forEach((h) => spy.observe(h));
  }

  if (!window.gsap) return;
  const { gsap } = window;
  if (window.ScrollTrigger) gsap.registerPlugin(window.ScrollTrigger);

  const mm = gsap.matchMedia();

  // ---------- reduced motion: show everything, animate nothing ----------
  mm.add('(prefers-reduced-motion: reduce)', () => {
    gsap.set('[data-reveal]', { opacity: 1, y: 0 });
    gsap.set('.bar', { height: 'auto' });
    document.querySelectorAll('[data-type]').forEach((el) => (el.textContent = el.dataset.type));
    document.querySelectorAll('.step').forEach((s) => s.classList.add('is-lit'));
    document.querySelectorAll('[data-count]').forEach((el) => (el.textContent = el.dataset.count));
    gsap.set('.chip', { opacity: 1, y: 0 });
  });

  // ---------- full motion ----------
  mm.add('(prefers-reduced-motion: no-preference)', () => {
    const ease = 'power3.out';

    // Ambient blobs: slow, staggered, never synchronised - same idea as the app's
    // mesh layer, which drifts position only (cheap) rather than animating opacity.
    document.querySelectorAll('.mesh span, .bar-mesh span').forEach((blob, i) => {
      gsap.to(blob, {
        x: `random(-40, 40)`,
        y: `random(-30, 30)`,
        duration: 14 + i * 3,
        repeat: -1,
        yoyo: true,
        ease: 'sine.inOut',
        delay: i * 0.6,
      });
    });

    // ---- hero: the bar drops open, then holds a short conversation ----
    const bar = document.querySelector('.bar');
    if (bar) {
      const idle = bar.querySelector('.bar-idle');
      const expanded = bar.querySelector('.bar-expanded');
      const said = bar.querySelector('[data-type="What is running on port three thousand?"]');
      const reply = bar.querySelector('[data-type="Port three thousand is held by node, process 8936."]');
      const caretA = bar.querySelector('.caret--a');
      const caretB = bar.querySelector('.caret--b');

      gsap.set(expanded, { display: 'none' });
      gsap.set('.hero-copy > *', { opacity: 0, y: 14 });
      gsap.set('.mock-frame', { opacity: 0, y: 20 });
      gsap.set('.chip', { opacity: 0, y: 6 });

      const label = bar.querySelector('.orb-label');
      const setLabel = (t) => {
        if (!label) return;
        label.textContent = t;
        // colour tracks the app: green while it listens, accent while it works
        label.style.color = t === 'LISTENING' ? 'var(--good)' : 'var(--accent)';
      };

      const type = (el, caret, text, speed = 0.032) => {
        const tl = gsap.timeline();
        if (!el) return tl;
        tl.set(caret, { opacity: 1 })
          .to(caret, { opacity: 0, duration: 0.45, repeat: -1, yoyo: true, ease: 'none' }, 0)
          .to(el, {
            duration: text.length * speed,
            ease: 'none',
            onUpdate() {
              const n = Math.round(this.progress() * text.length);
              el.textContent = text.slice(0, n);
            },
          }, 0)
          .set(caret, { opacity: 0 });
        return tl;
      };

      const hero = gsap.timeline({ defaults: { ease } });
      hero
        .to('.hero-copy > *', { opacity: 1, y: 0, duration: 0.7, stagger: 0.08 })
        .to('.mock-frame', { opacity: 1, y: 0, duration: 0.7 }, 0.15)
        // idle pill sits there first - that's how you actually meet DevBar
        .from('.bar-idle i', { scaleX: 0.2, opacity: 0, duration: 0.5 }, 0.5)
        .to({}, { duration: 0.45 })
        // ...then the shade unrolls: width settles fast, height keeps going
        .set(expanded, { display: 'block' })
        .set(idle, { display: 'none' })
        .fromTo(
          bar,
          { height: 22 },
          { height: () => expanded.scrollHeight + 22, duration: 0.42, ease: 'power2.out' }
        )
        .from('.tab', { opacity: 0, y: -4, duration: 0.3, stagger: 0.035 }, '-=0.15')
        .from('.orb i', { scale: 0.7, opacity: 0, duration: 0.45, stagger: 0.07 }, '-=0.25')
        .add(type(said, caretA, 'What is running on port three thousand?'), '+=0.25')
        .call(() => setLabel('THINKING'))
        .add(gsap.timeline().to('.orb .core', { opacity: 0.5, scale: 1.12, duration: 0.5, yoyo: true, repeat: 3, ease: 'sine.inOut' }), '+=0.05')
        .to('.chip', { opacity: 1, y: 0, duration: 0.3, stagger: 0.1 }, '-=1.4')
        .call(() => setLabel('SPEAKING'))
        .add(type(reply, caretB, 'Port three thousand is held by node, process 8936.'), '-=0.4')
        .call(() => setLabel('READY'), undefined, '+=0.4')
        .set(bar, { height: 'auto' });
    }

    if (!window.ScrollTrigger) return;

    // ---- section reveals ----
    document.querySelectorAll('[data-reveal]').forEach((el) => {
      gsap.to(el, {
        opacity: 1,
        y: 0,
        duration: 0.7,
        ease,
        scrollTrigger: { trigger: el, start: 'top 88%', once: true },
      });
    });
    gsap.set('[data-reveal]', { y: 18 });

    document.querySelectorAll('[data-reveal-group]').forEach((group) => {
      const kids = group.children;
      gsap.set(kids, { opacity: 0, y: 18 });
      gsap.to(kids, {
        opacity: 1,
        y: 0,
        duration: 0.6,
        ease,
        stagger: 0.07,
        scrollTrigger: { trigger: group, start: 'top 85%', once: true },
      });
    });

    // ---- counters ----
    document.querySelectorAll('[data-count]').forEach((el) => {
      const target = parseFloat(el.dataset.count);
      const decimals = (el.dataset.count.split('.')[1] || '').length;
      const suffix = el.dataset.suffix || '';
      const obj = { v: 0 };
      gsap.to(obj, {
        v: target,
        duration: 1.1,
        ease: 'power2.out',
        scrollTrigger: { trigger: el, start: 'top 92%', once: true },
        onUpdate: () => (el.textContent = obj.v.toFixed(decimals) + suffix),
      });
    });

    // ---- voice pipeline lights up as it scrolls through ----
    document.querySelectorAll('.step').forEach((step, i) => {
      window.ScrollTrigger.create({
        trigger: step,
        start: 'top 78%',
        once: true,
        onEnter: () => gsap.delayedCall(i * 0.12, () => step.classList.add('is-lit')),
      });
    });
  });
})();
