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
    document.querySelectorAll('.step').forEach((s) => s.classList.add('is-lit'));
    document.querySelectorAll('[data-count]').forEach((el) => (el.textContent = el.dataset.count));
    const video = document.querySelector('.hero-video');
    if (video) {
      video.removeAttribute('autoplay');
      video.pause();
      video.controls = true;
      const still = () => { video.currentTime = 5; }; // the bar open on Jarvis
      if (video.readyState >= 1) still(); else video.addEventListener('loadedmetadata', still, { once: true });
    }
  });

  // ---------- full motion ----------
  mm.add('(prefers-reduced-motion: no-preference)', () => {
    const ease = 'power3.out';

    // Ambient blobs: slow, staggered, never synchronised - same idea as the app's
    // mesh layer, which drifts position only (cheap) rather than animating opacity.
    document.querySelectorAll('.mesh span').forEach((blob, i) => {
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

    // ---- hero: copy and the recorded bar settle in ----
    gsap.set('.hero-copy > *', { opacity: 0, y: 14 });
    gsap.set('.mock-frame', { opacity: 0, y: 20 });
    gsap.timeline({ defaults: { ease } })
      .to('.hero-copy > *', { opacity: 1, y: 0, duration: 0.7, stagger: 0.08 })
      .to('.mock-frame', { opacity: 1, y: 0, duration: 0.7 }, 0.15);

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
