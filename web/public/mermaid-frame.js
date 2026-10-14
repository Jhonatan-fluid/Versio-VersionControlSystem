// The diagram frame's only job: take a diagram's text from the page that framed it, draw it with
// Mermaid in strict mode (labels sanitised, no click handlers), and report how tall it is.
// It receives text and returns a number; it is sandboxed without same-origin, so even a diagram
// that tricked Mermaid could reach no cookie, no session and nothing in the page.
(function () {
  'use strict';

  window.addEventListener('message', async function (event) {
    if (event.source !== window.parent) {
      return;
    }

    var data = event.data || {};
    if (typeof data.source !== 'string' || typeof data.id !== 'number') {
      return;
    }

    try {
      window.mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: data.dark ? 'dark' : 'default' });
      var result = await window.mermaid.render('diagram' + data.id, data.source);
      document.body.innerHTML = result.svg;
    } catch (e) {
      var failed = document.createElement('p');
      failed.className = 'failed';
      failed.textContent = 'This diagram could not be drawn.';
      document.body.replaceChildren(failed);
    }

    // Only a number goes back; '*' because a sandboxed frame cannot name its parent's origin.
    window.parent.postMessage({ id: data.id, height: Math.ceil(document.documentElement.scrollHeight) }, '*');
  });

  window.parent.postMessage({ ready: true }, '*');
})();
