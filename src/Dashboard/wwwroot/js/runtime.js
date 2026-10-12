// The runtime view: the environment's C4 deployment diagram, rendered by PlantUML when the dashboard was deployed
// (runtime/<env>.svg), inserted inline and updated in place from a payload the C# side builds (RuntimePayload).
//
// What this relies on in PlantUML's SVG (the deployment checks every render for it; README, "The runtime view"):
//   node          <g class="entity" data-qualified-name="sub.rg_tier.region_primary.plan_primary.app_ui_primary">:
//                 the alias is the last part; its rect (a database: its paths) is the box; its <image> is the slot
//   region        <g class="cluster" data-qualified-name="..."> : its first rect is the frame; its <image> is the slot
//   relationship  <g class="link" data-entity-1="<id of a node's g>" data-entity-2="..."> (or a <path id="a-to-b">,
//                 as Graphviz layouts name it): its path, polygon (head), text (label) and <image> (the slot)
// A slot is a transparent image of a fixed size that PlantUML laid out: it is hidden, and its rectangle is where the
// tile, the region's mark or the number line is drawn. Colours and lines are CSS (css/app.css, "Runtime view"): this
// module only sets data-rt-state and draws text and small shapes with classes.
//
// Links and trends come with the payload too. A link ({ href, title }) is drawn as a real <a> element (a new tab, rel
// noopener, its own <title>), so it takes the keyboard like any link; a redraw gives the focus back to the link that
// had it. A trend ({ points: 0..1, title }) is drawn as a small line after its number, with its words in a <title>.
// A box leads where its name leads: the name of a node, a region or a frame is such a link (nameLink), and a click
// anywhere else on the box opens the same address (openBox). There is one mechanism, the name's <a>: the box takes no
// place in the order of the keyboard, where the name link is its stop, and a click on any link inside the box is that
// link's own.
// A line's marks ([{ state, title }], the entries of a detailed health check) are drawn before its words, one small
// icon each: the shape says the state, the <title> the entry's name, state and words.
// A tile's deployment ({ state, title, link }, a deployment of the node's deployable that is in flight or just ended)
// is drawn as a small dot in the corner of the tile: filled and pulsing for executing, hollow for queued, a dot in a
// ring for waiting, small and still for ended. Its <title> is the sentence; it is a link where the payload has one.
// The same in words are the tile's last lines, the activity lines: their tone is their kind (waiting, deploying,
// queued, frozen, freeze, deployed, failed, canceled) and gives them their icon; like every line they are drawn only
// while the slot holds them, so a diagram whose slots have no rows for them shows the dot alone.
const SVG_NS = 'http://www.w3.org/2000/svg';

// How far "Fit to width" scales the diagram down before it scrolls sideways instead.
const FIT_FLOOR = 0.7;

function el(name, attributes, text) {
  const node = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes || {})) {
    if (value !== undefined && value !== null) node.setAttribute(key, String(value));
  }
  if (text !== undefined) node.textContent = text;
  return node;
}

function aliasOf(qualifiedName) {
  const parts = (qualifiedName || '').split('.');
  return parts[parts.length - 1];
}

// Hides the group's slot image and remembers its rectangle on the group.
function takeSlot(group) {
  const image = [...group.children].find(child => child.localName === 'image');
  if (!image) return;
  group.dataset.rtSlot = ['x', 'y', 'width', 'height'].map(key => parseFloat(image.getAttribute(key)) || 0).join(' ');
  image.remove();
}

// The layer to draw into: the slot's rectangle and an empty <g> at the end of the group.
function slotLayer(group) {
  if (!group.dataset.rtSlot) return null;
  const [x, y, width, height] = group.dataset.rtSlot.split(' ').map(Number);
  let layer = [...group.children].find(child => child.localName === 'g' && child.classList.contains('rt-slot'));
  if (layer) {
    // The layer is drawn again with every update: its classes of the state before go with its children, or a mark
    // that was "down" and is "serving" again keeps both and the rule that comes later in the stylesheet wins.
    layer.replaceChildren();
    layer.setAttribute('class', 'rt-slot');
  }
  else {
    layer = el('g', { class: 'rt-slot' });
    group.appendChild(layer);
  }
  return { x, y, width, height, layer };
}

function setTitle(group, text) {
  let title = [...group.children].find(child => child.localName === 'title');
  if (!title) {
    title = el('title');
    group.insertBefore(title, group.firstChild);
  }
  title.textContent = text || '';
}

// A link of the payload as an <a> element; the key finds it again after a redraw.
function anchor(link, key, cls) {
  const a = el('a', { class: `rt-link ${cls || ''}`, href: link.href, target: '_blank', rel: 'noopener', 'data-rt-key': key });
  a.appendChild(el('title', {}, link.title));
  return a;
}

// A trend as a small line in the given rectangle: oldest on the left, zero at the bottom, a dot on the last reading.
const TREND_WIDTH = 38;
function sparkline(layer, x, y, width, height, trend) {
  const points = trend.points || [];
  if (points.length < 2) return;
  const g = el('g', { class: 'rt-trend', role: 'img', 'aria-label': trend.title });
  g.appendChild(el('title', {}, trend.title));
  const inset = 2;
  const step = (width - 2 * inset) / (points.length - 1);
  const at = index => [x + inset + index * step, y + height - inset - points[index] * (height - 2 * inset)];
  g.appendChild(el('polyline', { class: 'rt-trend__line', points: points.map((_, index) => at(index).map(n => n.toFixed(2)).join(',')).join(' ') }));
  const [cx, cy] = at(points.length - 1);
  g.appendChild(el('circle', { class: 'rt-trend__now', cx: cx.toFixed(2), cy: cy.toFixed(2), r: 2 }));
  // A rectangle nobody sees takes the pointer, so the title shows anywhere over the trend.
  g.appendChild(el('rect', { class: 'rt-trend__hit', x, y, width, height }));
  layer.appendChild(g);
}

// The width of a text once drawn; an estimate while the diagram is not laid out (a hidden tab).
function widthOf(text) {
  try {
    const measured = text.getComputedTextLength();
    if (measured > 0) return measured;
  } catch {
    // Not rendered: fall back to the estimate.
  }
  return (text.textContent || '').length * (parseFloat(text.getAttribute('font-size')) || 12) * 0.58;
}

// Small icons, 12 by 12, centred on (cx, cy): the shape says the state as well as the colour.
function icon(kind, cx, cy, cls) {
  const g = el('g', { class: `rt-icon ${cls || ''}`, transform: `translate(${cx - 6} ${cy - 6})` });
  const add = (name, attributes) => g.appendChild(el(name, attributes));
  switch (kind) {
    case 'healthy':
    case 'ok':
    case 'serving':
      add('circle', { cx: 6, cy: 6, r: 6, class: 'rt-icon__fill' });
      add('path', { d: 'M3.2 6.3l1.9 1.9 3.8-4.1', class: 'rt-icon__mark' });
      break;
    case 'unhealthy':
    case 'warn':
    case 'down':
      add('path', { d: 'M6 .4l5.8 10.4H.2z', class: 'rt-icon__fill' });
      add('path', { d: 'M6 4v3.4M6 9.2v.2', class: 'rt-icon__mark' });
      break;
    case 'unreachable':
      add('circle', { cx: 6, cy: 6, r: 6, class: 'rt-icon__fill' });
      add('path', { d: 'M3.8 3.8l4.4 4.4M8.2 3.8l-4.4 4.4', class: 'rt-icon__mark' });
      break;
    case 'insync':
      add('path', { d: 'M2 4.3h8M2 7.7h8', class: 'rt-icon__line' });
      break;
    case 'differs':
      add('path', { d: 'M2 4.3h8M2 7.7h8M8 1.5L4 10.5', class: 'rt-icon__line' });
      break;
    case 'standby':
      add('circle', { cx: 6, cy: 6, r: 5.25, class: 'rt-icon__ring' });
      add('path', { d: 'M4.6 4v4M7.4 4v4', class: 'rt-icon__line' });
      break;
    case 'neutral':
      add('circle', { cx: 6, cy: 6, r: 5.25, class: 'rt-icon__ring' });
      add('path', { d: 'M3.5 6h5', class: 'rt-icon__line' });
      break;
    // The activity lines. In flight: the shapes of the deployment's dot.
    case 'act-waiting':
      add('circle', { cx: 6, cy: 6, r: 5.25, class: 'rt-icon__ring' });
      add('circle', { cx: 6, cy: 6, r: 2.25, class: 'rt-icon__dot' });
      break;
    case 'act-deploying':
      add('circle', { cx: 6, cy: 6, r: 5, class: 'rt-icon__dot' });
      break;
    case 'act-queued':
      add('circle', { cx: 6, cy: 6, r: 4.25, class: 'rt-icon__ring' });
      break;
    case 'act-frozen': // a snowflake: a deployment freeze, in force or coming
      add('path', { d: 'M6 .8v10.4M1.5 3.4l9 5.2M10.5 3.4l-9 5.2', class: 'rt-icon__line' });
      break;
    case 'act-deployed':
      add('path', { d: 'M2.2 6.4l2.5 2.5 5.1-5.6', class: 'rt-icon__line' });
      break;
    case 'act-failed':
      add('path', { d: 'M2.8 2.8l6.4 6.4M9.2 2.8l-6.4 6.4', class: 'rt-icon__line' });
      break;
    case 'act-canceled':
      add('path', { d: 'M2.5 6h7', class: 'rt-icon__line' });
      break;
    default: // checking, unknown
      add('circle', { cx: 6, cy: 6, r: 5.25, class: 'rt-icon__ring' });
      add('path', { d: 'M3.6 6h.1M5.95 6h.1M8.3 6h.1', class: 'rt-icon__line' });
      break;
  }
  return g;
}

// The marks of a line: one icon per entry, each with its own title and a rectangle that takes the pointer.
const MARK_STEP = 14;
const MARK_ICON = { healthy: 'ok', degraded: 'warn', failed: 'unreachable' };
function marks(group, left, baseline, list) {
  list.forEach((mark, index) => {
    const cx = left + index * MARK_STEP + 6;
    const g = el('g', { class: `rt-check rt-check--${mark.state}`, role: 'img', 'aria-label': mark.title });
    g.appendChild(el('title', {}, mark.title));
    g.appendChild(icon(MARK_ICON[mark.state] || 'unknown', cx, baseline - 4, 'rt-row__icon'));
    g.appendChild(el('rect', { class: 'rt-trend__hit', x: cx - 7, y: baseline - 11, width: MARK_STEP, height: 14 }));
    group.appendChild(g);
  });
}

// One centred row: an optional icon or the line's marks, the text (in pieces where a piece is a link), an optional
// trend.
function row(layer, centre, baseline, line, cls, iconKind, size, key) {
  const group = el('g', { class: `rt-row ${cls}` });
  layer.appendChild(group);
  const words = el('text', { y: baseline, 'font-size': size || 11.5, class: 'rt-row__text' });
  if (line.parts) {
    line.parts.forEach((part, index) => {
      if (part.link) {
        const a = anchor(part.link, `${key}-${index}`);
        a.appendChild(document.createTextNode(part.text));
        words.appendChild(a);
      } else {
        words.appendChild(el('tspan', {}, part.text));
      }
    });
  } else {
    words.textContent = line.text;
  }
  group.appendChild(words);
  const checks = line.marks && line.marks.length > 0 ? line.marks : null;
  const iconWidth = checks ? checks.length * MARK_STEP + 4 : iconKind ? 16 : 0;
  const trendWidth = line.trend ? TREND_WIDTH + 6 : 0;
  const textWidth = widthOf(words);
  const left = centre - (iconWidth + textWidth + trendWidth) / 2;
  words.setAttribute('x', left + iconWidth);
  if (checks) marks(group, left, baseline, checks);
  else if (iconKind) group.insertBefore(icon(iconKind, left + 6, baseline - 4, 'rt-row__icon'), words);
  if (line.trend) sparkline(group, left + iconWidth + textWidth + 6, baseline - 10, TREND_WIDTH, 12, line.trend);
}

// The name of a node, a region or a frame, which PlantUML drew, as a link (or as plain text again when the payload
// has no link for it). The group is marked: a box with a name link is one a click opens (openBox).
function linkName(group, link, key) {
  let a = [...group.children].find(child => child.localName === 'a' && child.classList.contains('rt-name-link'));
  group.classList.toggle('rt-linked', Boolean(link));
  if (!link) {
    if (a) a.replaceWith(...[...a.children].filter(child => child.localName === 'text'));
    return;
  }
  if (!a) {
    const names = [...group.children].filter(child => child.localName === 'text' && child.classList.contains('rt-name'));
    if (names.length === 0) return;
    a = anchor(link, key, 'rt-name-link');
    names[0].before(a);
    a.append(...names);
  }
  a.setAttribute('href', link.href);
  a.querySelector('title').textContent = link.title;
}

// A click on a box goes where its name goes, in a new tab as the name's link does. A click on a link inside the box
// (the name itself, the badge, a number, the mark of a deployment, an address PlantUML drew) is that link's own and
// is left alone; so is the click that ends a selection of text.
function openBox(event) {
  const target = event.target;
  if (event.defaultPrevented || event.button !== 0 || !target || !target.closest) return;
  if (target.closest('a')) return;
  const group = target.closest('g.rt-linked');
  if (!group) return;
  const name = [...group.children].find(child => child.localName === 'a' && child.classList.contains('rt-name-link'));
  const href = name && name.getAttribute('href');
  if (!href) return;
  const selection = window.getSelection && window.getSelection();
  if (selection && !selection.isCollapsed && group.contains(selection.anchorNode)) return;
  window.open(href, '_blank', 'noopener');
}

const LINE_ICON = {
  insync: 'insync', differs: 'differs', unknown: 'unknown', serving: 'serving', ok: 'ok', warn: 'warn',
  // The activity lines: what is being deployed to the node's deployable, a deployment freeze, the last deployment.
  waiting: 'act-waiting', deploying: 'act-deploying', queued: 'act-queued', frozen: 'act-frozen', freeze: 'act-frozen',
  deployed: 'act-deployed', failed: 'act-failed', canceled: 'act-canceled',
};
const BAR = { healthy: 12, unhealthy: 6, unreachable: 2.5 };

function drawTile(group, tile) {
  const slot = slotLayer(group);
  if (!slot) return;
  const { x, y, width, height, layer } = slot;
  layer.classList.add('rt-tile', `rt-tile--${tile.state}`);
  const centre = x + width / 2;

  // Row 1: the badge (icon and word) and the facts next to it. The badge is a link where the payload has one.
  const badge = el('g', { class: 'rt-badge' });
  if (tile.link) layer.appendChild(anchor(tile.link, `${tile.alias}-state`)).appendChild(badge);
  else layer.appendChild(badge);
  const label = el('text', { y: y + 15, 'font-size': 12, class: 'rt-badge__text' }, tile.label);
  badge.appendChild(label);
  const badgeWidth = 26 + widthOf(label);
  let facts = null;
  if (tile.facts) {
    facts = el('text', { y: y + 15, 'font-size': 12, class: 'rt-tile__facts' }, tile.facts);
    layer.appendChild(facts);
  }
  const factsWidth = facts ? 8 + widthOf(facts) : 0;
  const left = centre - (badgeWidth + factsWidth) / 2;
  badge.insertBefore(el('rect', { x: left, y: y + 1, width: badgeWidth, height: 19, rx: 9.5, class: 'rt-badge__back' }), label);
  badge.insertBefore(icon(tile.state, left + 11, y + 10.5, 'rt-badge__icon'), label);
  label.setAttribute('x', left + 20);
  if (facts) facts.setAttribute('x', left + badgeWidth + 8);

  // The lines, as many as the slot holds above the history strip.
  const history = tile.history;
  const bottom = history ? y + height - 16 : y + height;
  let baseline = y + 38;
  (tile.lines || []).forEach((line, index) => {
    if (baseline > bottom) return;
    const tone = line.tone || 'plain';
    row(layer, centre, baseline, line, `rt-line rt-line--${tone}`, LINE_ICON[tone], tone === 'strong' ? 12.5 : 11.5, `${tile.alias}-line${index}`);
    baseline += 15;
  });

  // The history strip, oldest on the left: the height says the state as well as the colour. Its 30 bars take the
  // slot's width, 8 px each at most.
  if (history) {
    const slots = 30, step = Math.min(8, (width + 2) / slots), bar = step - 2;
    const start = centre - (slots * step - (step - bar)) / 2;
    const base = y + height - 1;
    const shown = history.slice(-slots);
    for (let index = 0; index < slots; index++) {
      const state = shown[index - (slots - shown.length)];
      const h = state ? (BAR[state] || 2.5) : 2;
      layer.appendChild(el('rect', {
        x: start + index * step, y: base - h, width: bar, height: h, rx: 1,
        class: `rt-bar rt-bar--${state || 'empty'}`,
      }));
    }
  }
}

// The mark of a deployment, on a white disc so it reads on a box of any state. It has a layer of its own, after the
// tile's: in the corner of the slot, or of the node's box for a node without a slot. The pulse is CSS, and only where
// the viewer has not asked for less motion.
function drawDeployment(group, tile) {
  let layer = [...group.children].find(child => child.localName === 'g' && child.classList.contains('rt-deploy-layer'));
  const mark = tile.deployment;
  if (!mark) {
    if (layer) layer.remove();
    return;
  }
  let cx, cy;
  if (group.dataset.rtSlot) {
    const [x, y, width] = group.dataset.rtSlot.split(' ').map(Number);
    cx = x + width - 9;
    cy = y + 10.5;
  } else {
    const box = [...group.children].find(child => child.localName === 'rect' && child.classList.contains('rt-box'));
    if (!box) return;
    cx = (parseFloat(box.getAttribute('x')) || 0) + (parseFloat(box.getAttribute('width')) || 0) - 13;
    cy = (parseFloat(box.getAttribute('y')) || 0) + 13;
  }
  if (layer) layer.replaceChildren();
  else layer = el('g', { class: 'rt-deploy-layer' });
  group.appendChild(layer);
  const dot = el('g', { class: `rt-deploy rt-deploy--${mark.state}`, role: 'img', 'aria-label': mark.title });
  if (mark.link) layer.appendChild(anchor(mark.link, `${tile.alias}-deployment`)).appendChild(dot);
  else layer.appendChild(dot).appendChild(el('title', {}, mark.title));
  const add = (cls, r) => dot.appendChild(el('circle', { class: cls, cx, cy, r }));
  if (mark.state === 'executing') add('rt-deploy__pulse', 7);
  add('rt-deploy__back', 8.5);
  switch (mark.state) {
    case 'executing':
      add('rt-deploy__fill', 5.5);
      break;
    case 'queued':
      add('rt-deploy__ring', 4.75);
      break;
    case 'waiting':
      add('rt-deploy__ring', 5.75);
      add('rt-deploy__fill', 2.5);
      break;
    default: // ended
      add('rt-deploy__fill', 2.5);
      break;
  }
}

function drawRegion(group, mark) {
  const slot = slotLayer(group);
  if (!slot) return;
  const { x, y, width, height, layer } = slot;
  layer.classList.add('rt-mark', `rt-mark--${mark.state}`);
  const centre = x + width / 2;
  const text = el('text', { y: y + height / 2 + 4, 'font-size': 11.5, class: 'rt-mark__text' }, mark.label);
  layer.appendChild(text);
  const pill = mark.state !== 'neutral';
  const contentWidth = (pill ? 16 : 0) + widthOf(text);
  const left = centre - contentWidth / 2;
  text.setAttribute('x', left + (pill ? 16 : 0));
  if (pill) {
    layer.insertBefore(el('rect', { x: left - 8, y: y + 1, width: contentWidth + 16, height: height - 2, rx: (height - 2) / 2, class: 'rt-mark__back' }), text);
    const kind = { serving: 'serving', standby: 'standby', down: 'unreachable' }[mark.state] || 'checking';
    layer.insertBefore(icon(kind, left + 6, y + height / 2, 'rt-mark__icon'), text);
  }
}

function drawEdge(group, mark) {
  if (mark.number === undefined || mark.number === null) return;
  const slot = slotLayer(group);
  if (!slot) return;
  const { x, y, width, layer } = slot;
  layer.classList.add('rt-number');
  // A counted number gets a solid frame; the dash of a relationship nobody counts keeps the dashed one.
  if (mark.number !== '–') layer.classList.add('rt-number--counted');
  const centre = x + width / 2;
  // The number line: the calls of the last minute in a frame, with their trend; the role under it. The number is a
  // link where the payload has one.
  const line = el('text', { y: y + 13, 'font-size': 12, class: 'rt-number__text' });
  line.appendChild(el('tspan', { class: 'rt-number__value', 'font-size': 13 }, mark.number));
  line.appendChild(el('tspan', {}, ` ${mark.unit || ''}`));
  if (mark.link) layer.appendChild(anchor(mark.link, `${mark.id}-number`)).appendChild(line);
  else layer.appendChild(line);
  const lineWidth = widthOf(line);
  const trendWidth = mark.trend ? TREND_WIDTH + 5 : 0;
  const left = centre - (lineWidth + trendWidth) / 2;
  line.setAttribute('x', left);
  layer.insertBefore(el('rect', { x: left - 7, y: y, width: lineWidth + trendWidth + 14, height: 17, rx: 4, class: 'rt-number__hook' }), layer.firstChild);
  if (mark.trend) sparkline(layer, left + lineWidth + 5, y + 2.5, TREND_WIDTH, 12, mark.trend);
  if (mark.text) {
    const role = el('text', { y: y + 30, 'font-size': 11, class: 'rt-number__role' }, mark.text);
    layer.appendChild(role);
    role.setAttribute('x', centre - widthOf(role) / 2);
  }
}

// Inserts the diagram into the host element. Returns null, or why it cannot be shown.
export function mount(host, svgText) {
  const parsed = new DOMParser().parseFromString(svgText, 'image/svg+xml');
  const root = parsed.documentElement;
  if (!root || root.localName !== 'svg' || parsed.getElementsByTagName('parsererror').length > 0) {
    host.replaceChildren();
    return 'The diagram is not a valid SVG.';
  }
  const svg = document.importNode(root, true);
  // PlantUML fixes the size in an inline style; the page sizes it (actual size, or fitted to the width).
  svg.removeAttribute('style');
  svg.classList.add('runtime-svg');
  // Fitted to the width, it never shrinks below FIT_FLOOR of its size: the tiles' words (11.5 px) stay above 8 px,
  // and a narrower window scrolls instead.
  const natural = parseFloat(svg.getAttribute('width')) || (svg.viewBox && svg.viewBox.baseVal ? svg.viewBox.baseVal.width : 0);
  if (natural > 0) svg.style.setProperty('--rt-min-width', `${Math.round(natural * FIT_FLOOR)}px`);
  const ids = new Map();
  for (const group of svg.querySelectorAll('g[data-qualified-name]')) {
    if (group.id) ids.set(group.id, aliasOf(group.getAttribute('data-qualified-name')));
  }
  for (const group of svg.querySelectorAll('g.entity[data-qualified-name]')) {
    group.dataset.rtAlias = aliasOf(group.getAttribute('data-qualified-name'));
    group.classList.add('rt-node');
    let named = false;
    for (const shape of group.children) {
      if (shape.localName === 'rect' || shape.localName === 'path') {
        shape.classList.add(shape.getAttribute('fill') === 'none' ? 'rt-box-line' : 'rt-box');
      } else if (shape.localName === 'text' && !named) {
        // The name is the bold text PlantUML writes first (one <text> per word); the type follows in italics.
        if (shape.getAttribute('font-weight') === '700') shape.classList.add('rt-name');
        else named = true;
      }
    }
    takeSlot(group);
  }
  for (const group of svg.querySelectorAll('g.cluster[data-qualified-name]')) {
    group.dataset.rtAlias = aliasOf(group.getAttribute('data-qualified-name'));
    group.classList.add('rt-region');
    const frame = [...group.children].find(child => child.localName === 'rect');
    if (frame) frame.classList.add('rt-frame');
    // The frame's name is the bold text PlantUML writes first, at the title's size; its type follows, smaller.
    const texts = [...group.children].filter(child => child.localName === 'text');
    const size = texts.length > 0 ? texts[0].getAttribute('font-size') : null;
    for (const text of texts) {
      if (text.getAttribute('font-weight') !== '700' || text.getAttribute('font-size') !== size) break;
      text.classList.add('rt-name');
    }
    takeSlot(group);
  }
  for (const group of svg.querySelectorAll('g.link')) {
    const named = [...group.children].find(child => child.localName === 'path' && child.id);
    const from = ids.get(group.getAttribute('data-entity-1'));
    const to = ids.get(group.getAttribute('data-entity-2'));
    const id = from && to ? `${from}-to-${to}` : named ? named.id : null;
    if (!id) continue;
    group.dataset.rtEdge = id;
    group.classList.add('rt-edge');
    for (const child of group.children) {
      if (child.localName === 'path') child.classList.add('rt-edge-line');
      else if (child.localName === 'polygon') child.classList.add('rt-edge-head');
    }
    takeSlot(group);
  }
  svg.addEventListener('click', openBox);
  host.replaceChildren(svg);
  return null;
}

// Applies one payload (JSON text). Returns what the payload names and the diagram lacks.
export function update(host, json) {
  const svg = host.querySelector('svg.runtime-svg');
  if (!svg) return ['the diagram'];
  const payload = JSON.parse(json);
  const missing = [];
  // The links are drawn again with every update: the one that had the focus gets it back.
  const active = document.activeElement;
  const focused = active && svg.contains(active) ? active.getAttribute('data-rt-key') : null;
  const find = (selector, value) => [...svg.querySelectorAll(selector)].find(group => group.dataset[value.key] === value.name);
  for (const tile of payload.nodes || []) {
    const group = find('g.rt-node', { key: 'rtAlias', name: tile.alias });
    if (!group) { missing.push(`node ${tile.alias}`); continue; }
    group.dataset.rtState = tile.state;
    setTitle(group, tile.title);
    drawTile(group, tile);
    drawDeployment(group, tile);
    linkName(group, tile.nameLink, `${tile.alias}-name`);
  }
  for (const mark of payload.regions || []) {
    const group = find('g.rt-region', { key: 'rtAlias', name: mark.alias });
    if (!group) { missing.push(`region ${mark.alias}`); continue; }
    group.dataset.rtState = mark.state;
    drawRegion(group, mark);
    linkName(group, mark.nameLink, `${mark.alias}-name`);
  }
  // The boxes with a name and nothing else to update: the frames that are no region, and the browser.
  for (const name of payload.names || []) {
    const group = find('g.rt-region, g.rt-node', { key: 'rtAlias', name: name.alias });
    if (!group) { missing.push(`frame ${name.alias}`); continue; }
    linkName(group, name.nameLink, `${name.alias}-name`);
  }
  for (const mark of payload.edges || []) {
    const group = find('g.rt-edge', { key: 'rtEdge', name: mark.id });
    if (!group) { missing.push(`relationship ${mark.id}`); continue; }
    group.dataset.rtState = mark.state;
    setTitle(group, mark.title);
    drawEdge(group, mark);
  }
  if (focused) {
    const again = [...svg.querySelectorAll('a[data-rt-key]')].find(a => a.getAttribute('data-rt-key') === focused);
    if (again) again.focus({ preventScroll: true });
  }
  return missing;
}

export function clear(host) {
  host.replaceChildren();
}
