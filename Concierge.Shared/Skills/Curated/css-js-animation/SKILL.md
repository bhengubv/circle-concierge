---
name: css-js-animation
description: Provider-neutral CSS and JavaScript animation workflow for keyframes, transitions, scroll animation, SVG/canvas motion, choreography, performance, and reduced-motion accessibility.
category: Creative Technology
profile: CSS and JavaScript Animation
---

# CSS and JavaScript Animation

Use this skill when the user needs product-grade frontend animation with CSS, JavaScript, SVG, canvas, or animation libraries.

## Workflow

1. Identify the animation purpose: orientation, continuity, feedback, reveal, storytelling, delight, loading, transition, or data emphasis.
2. Choose the lightest reliable technique: CSS transition, CSS keyframes, Web Animations API, requestAnimationFrame, SVG, canvas, scroll timeline, or animation library.
3. Define choreography: trigger, duration, easing, delay, stagger, interruption behavior, responsive behavior, and reduced-motion fallback.
4. Keep animation layout-safe by preferring transform and opacity, avoiding forced reflow, and budgeting CPU/GPU cost.
5. Test with keyboard, touch, pointer, screen readers, reduced motion, low-end devices, and browser differences.

## Coverage

- CSS transitions, keyframes, transforms, custom properties, easing, staggered reveals, and responsive animation tokens.
- JavaScript animation with Web Animations API, requestAnimationFrame, IntersectionObserver, ResizeObserver, and scroll-linked effects.
- SVG path animation, canvas animation, sprite animation, Lottie/Rive integration, and product microinteractions.
- Library planning for GSAP, Motion, Anime.js, Framer Motion-style patterns, Three.js handoff, and Blazor/JS interop.
- Performance profiling, reduced-motion design, graceful fallback, and animation QA.

## Guardrails

- Do not animate purely for spectacle when it slows the user down or hides state.
- Avoid animating layout properties such as top, left, width, height, and margin unless there is a measured reason.
- Provide `prefers-reduced-motion` behavior for non-essential animation.
- Avoid scroll-jacking, inaccessible carousels, flashing, and motion that causes discomfort.
- Preserve product clarity over visual cleverness.

## Output

Produce animation specs, CSS/JS implementation plans, timing/easing tokens, reduced-motion fallbacks, performance notes, QA checklists, and code-ready interaction choreography.
