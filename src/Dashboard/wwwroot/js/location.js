// The view in the address: #runtime or #runtime/<environment> opens the runtime view, #cluster the cluster view (where
// the topology has a cluster), anything else the health view. #runtime/<environment>/database is the runtime view
// zoomed into the environment's database, #runtime/<environment>/app/<deployable> the same zoomed into the box of
// that deployable.
// The page replaces the hash as the viewer switches (no history entry per click) and follows a hash typed or linked.
// Zooming into the database or an application and back out is a step of the history, so the browser's Back zooms out.
let listener = null;

export function hash() {
  return decodeURIComponent(location.hash.replace(/^#/, ''));
}

export function setHash(value, step) {
  const target = value ? `#${value.split('/').map(encodeURIComponent).join('/')}` : location.pathname + location.search;
  if (step) history.pushState(history.state, '', target);
  else history.replaceState(history.state, '', target);
}

// Calls OnHashChanged(hash) on the .NET object when the hash changes; returns the hash now.
export function subscribe(dotNetObject) {
  unsubscribe();
  listener = () => dotNetObject.invokeMethodAsync('OnHashChanged', hash());
  window.addEventListener('hashchange', listener);
  return hash();
}

export function unsubscribe() {
  if (listener) {
    window.removeEventListener('hashchange', listener);
    listener = null;
  }
}
