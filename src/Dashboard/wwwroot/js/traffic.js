// The traffic button: representative requests from this browser to the apps' public addresses, so the numbers on the
// runtime diagram move. A request is a plain GET in mode no-cors: the browser sends it like a link it follows and needs
// no CORS answer, and its response stays opaque (an answer counts as answered, a network failure as failed). A request
// with no answer within answerWithinSeconds is given up and counts as unanswered, so every request sent ends as one of
// the three, also after the last one was sent: the page keeps reading status() until none is on its way.
let run = null;

export function start(addresses, perSecond, seconds, answerWithinSeconds) {
  stop();
  const current = { sent: 0, answered: 0, failed: 0, unanswered: 0, sending: true, stopped: false };
  run = current;
  const end = Date.now() + seconds * 1000;
  const pause = 1000 / perSecond;
  (async () => {
    let index = 0;
    while (!current.stopped && Date.now() < end) {
      const address = addresses[index++ % addresses.length];
      current.sent++;
      fetch(address, { mode: 'no-cors', cache: 'no-store', credentials: 'omit', signal: AbortSignal.timeout(answerWithinSeconds * 1000) })
        .then(() => current.answered++, (error) => (error?.name === 'TimeoutError' ? current.unanswered++ : current.failed++));
      await new Promise((resolve) => setTimeout(resolve, pause));
    }
    current.sending = false;
  })();
}

// [sent, answered, failed, unanswered, sending (1 or 0)]; nothing sent and not sending before the first press.
export function status() {
  if (!run) return [0, 0, 0, 0, 0];
  return [run.sent, run.answered, run.failed, run.unanswered, run.sending ? 1 : 0];
}

// Sends no more; what is on its way is still counted.
export function stop() {
  if (run) run.stopped = true;
}
