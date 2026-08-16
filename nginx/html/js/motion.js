import { prefersReducedMotion, qsa } from './util.js';

// Инерционный скролл: apply(pos) вызывается каждый кадр со сглаженной позицией.
export function initParallax(apply) {
  if (prefersReducedMotion()) { apply(0, true); return () => {}; }

  let cur = 0;
  let tgt = window.scrollY || 0;
  let raf = 0;
  const onScroll = () => { tgt = window.scrollY || 0; };

  window.addEventListener('scroll', onScroll, { passive: true });
  const loop = () => {
    cur += (tgt - cur) * 0.09;
    if (Math.abs(tgt - cur) < 0.05) cur = tgt;
    apply(cur, false);
    raf = requestAnimationFrame(loop);
  };
  raf = requestAnimationFrame(loop);

  return () => { cancelAnimationFrame(raf); window.removeEventListener('scroll', onScroll); };
}

// Элементы с [data-reveal] всплывают, когда доезжают до экрана.
// Значение атрибута — номер в очереди, он же задержка.
export function initReveal() {
  if (prefersReducedMotion()) return () => {};

  const vh = window.innerHeight || 800;
  const hidden = qsa('[data-reveal]').filter((el) => {
    if (el.getBoundingClientRect().top <= vh * 0.92) return false;
    const n = parseInt(el.getAttribute('data-reveal'), 10) || 0;
    el.classList.add('is-hidden');
    el.style.transitionDelay = (n * 90) + 'ms';
    return true;
  });
  if (!hidden.length) return () => {};

  const io = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (!entry.isIntersecting) return;
      entry.target.classList.remove('is-hidden');
      io.unobserve(entry.target);
    });
  }, { threshold: 0.12 });

  hidden.forEach((el) => io.observe(el));
  return () => io.disconnect();
}
