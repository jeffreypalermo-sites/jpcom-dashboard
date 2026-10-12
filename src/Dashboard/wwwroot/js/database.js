// The database view: the tables of the environment's database as a diagram, rendered by PlantUML when the dashboard
// was deployed (runtime/<env>.database.svg) and inserted inline as the runtime diagram is (readSvg of js/runtime.js).
//
// What this relies on in PlantUML's SVG (README, "The database view"):
//   table   <g class="entity" data-qualified-name="db.t_dbo_Employee">: one of its <text> elements holds exactly the
//           row count, "1,234 rows" or "1 row", with a no-break space where the space is (PlantUML writes one <text>
//           per word, so the deployment joins the words of a line with no-break spaces)
// The C# side decides every count (DatabaseText.Counts); this module only writes it, as text.
import { leaveView, readSvg } from './runtime.js';

const NBSP = ' ';
const ROW_COUNT = /^[0-9][0-9,]* rows?$/;

// Inserts the diagram into the host element, at its actual size. Returns null, or why it cannot be shown.
export function mount(host, svgText) {
  const svg = readSvg(svgText);
  if (!svg) {
    host.replaceChildren();
    return 'The diagram is not a valid SVG.';
  }
  svg.classList.add('database-svg');
  host.replaceChildren(svg);
  return null;
}

// Writes a row count into every table named: names[i] is a table's data-qualified-name, texts[i] its count in words.
// A table the diagram lacks, and one without a row count drawn, is left alone.
export function counts(host, names, texts) {
  const svg = host.querySelector('svg.database-svg');
  if (!svg) return;
  const wanted = new Map(names.map((name, index) => [name, texts[index]]));
  for (const group of svg.querySelectorAll('g.entity[data-qualified-name]')) {
    const count = wanted.get(group.getAttribute('data-qualified-name'));
    if (count === undefined) continue;
    const drawn = [...group.querySelectorAll('text')].find(text => ROW_COUNT.test((text.textContent || '').replaceAll(NBSP, ' ').trim()));
    if (!drawn) continue;
    const words = count.replaceAll(' ', NBSP);
    if (drawn.textContent === words) continue;
    // PlantUML fixed the width of the words it drew: another number has another width.
    drawn.removeAttribute('textLength');
    drawn.removeAttribute('lengthAdjust');
    drawn.textContent = words;
  }
}

// "Back to the runtime diagram": the view shrinks and fades as every view a box led into does (leaveView).
export function leave(section) {
  return leaveView(section);
}
