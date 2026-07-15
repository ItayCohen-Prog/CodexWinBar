(() => {
  const status = document.querySelector('#copy-status');
  const resetTimers = new WeakMap();

  async function copyText(value) {
    if (navigator.clipboard?.writeText && window.isSecureContext) {
      try {
        await navigator.clipboard.writeText(value);
        return;
      } catch (_) {
        // Fall through to the selection-based copy path.
      }
    }

    const field = document.createElement('textarea');
    field.value = value;
    field.setAttribute('readonly', '');
    field.style.position = 'fixed';
    field.style.inset = '0 auto auto -9999px';
    document.body.append(field);
    field.select();
    field.setSelectionRange(0, field.value.length);
    const copied = document.execCommand('copy');
    field.remove();

    if (!copied) {
      throw new Error('Copy command was not accepted by the browser.');
    }
  }

  document.querySelectorAll('[data-copy-target]').forEach((button) => {
    const target = document.getElementById(button.dataset.copyTarget);
    const label = button.querySelector('span');
    const defaultLabel = label?.textContent || 'Copy';

    if (!target) return;
    button.hidden = false;

    button.addEventListener('click', async () => {
      window.clearTimeout(resetTimers.get(button));

      try {
        await copyText(target.textContent.trim());
        if (label) label.textContent = 'Copied';
        if (status) status.textContent = 'Install command copied to the clipboard.';
      } catch (_) {
        if (label) label.textContent = 'Select command';
        if (status) status.textContent = 'Copy was blocked. Select the command above and copy it manually.';
        target.focus?.();
      }

      const timer = window.setTimeout(() => {
        if (label) label.textContent = defaultLabel;
        if (status) status.textContent = '';
      }, 2400);
      resetTimers.set(button, timer);
    });
  });
})();
