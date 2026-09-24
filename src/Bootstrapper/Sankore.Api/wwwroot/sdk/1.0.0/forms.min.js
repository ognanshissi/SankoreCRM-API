/**
 * Sankore CRM — forms.js SDK v1.0.0
 *
 * Lightweight, zero-dependency SDK for capturing leads from web forms.
 *
 * Usage (attach to existing form):
 *   <script src="/sdk/v1/forms.min.js"
 *           data-sankore-key="YOUR_PUBLIC_KEY"
 *           data-sankore-form="#contact-form"
 *           async defer></script>
 *
 * Usage (hosted form rendered by CRM):
 *   <script src="/sdk/v1/forms.min.js"
 *           data-sankore-key="YOUR_PUBLIC_KEY"
 *           data-sankore-render="true"
 *           async defer></script>
 *
 * No dependencies. < 8 KB gzipped. Compatible with last 2 years of browsers.
 * Each version is immutable (served with SRI hash).
 *
 * @license MIT
 */
;(function () {
  'use strict';

  // ── Constants ───────────────────────────────────────────────────────
  var SDK_VERSION = '1.0.0';
  var PING_KEY = '__sankore_pinged';
  var UTM_KEY = '__sankore_utm';
  var API_BASE = '';  // same origin by default; override via data-sankore-api

  // ── Boot ────────────────────────────────────────────────────────────
  var script = document.currentScript;
  if (!script) return;

  var publicKey    = script.getAttribute('data-sankore-key');
  var formSelector = script.getAttribute('data-sankore-form');
  var renderMode   = script.getAttribute('data-sankore-render') === 'true';
  var apiBase      = script.getAttribute('data-sankore-api') || API_BASE;
  var preventDefault = script.getAttribute('data-sankore-prevent') !== 'false'; // default true

  if (!publicKey) {
    console.warn('[Sankore] data-sankore-key is required.');
    return;
  }

  // ── UTM capture (first page of session) ─────────────────────────────
  captureUtm();

  // ── Wait for DOM ────────────────────────────────────────────────────
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

  // ── Init ─────────────────────────────────────────────────────────────
  function init() {
    sendPing();

    if (renderMode) {
      renderHostedForm();
    } else if (formSelector) {
      attachToForm();
    } else {
      console.warn('[Sankore] No data-sankore-form or data-sankore-render specified.');
    }
  }

  // ── Ping (once per session) ─────────────────────────────────────────
  function sendPing() {
    if (sessionStorage.getItem(PING_KEY + '_' + publicKey)) return;

    fetch(apiBase + '/api/ingest/web/' + publicKey + '/ping', {
      method: 'POST',
      mode: 'cors',
      keepalive: true,
      headers: { 'Content-Type': 'application/json' }
    }).then(function () {
      sessionStorage.setItem(PING_KEY + '_' + publicKey, '1');
    }).catch(noop);
  }

  // ── Attach to existing form ─────────────────────────────────────────
  function attachToForm() {
    var form = document.querySelector(formSelector);
    if (!form) {
      console.warn('[Sankore] Form not found: ' + formSelector);
      return;
    }

    var formLoadedAt = new Date().toISOString();

    form.addEventListener('submit', function (e) {
      if (preventDefault) e.preventDefault();

      var fields = collectFields(form);
      var consent = getConsent(form);
      var captchaToken = getCaptchaToken(form);

      var payload = buildPayload(fields, consent, captchaToken, formLoadedAt);

      var sendPromise = sendLead(payload);

      if (preventDefault) {
        sendPromise.then(function (res) {
          if (res.ok || res.status === 202) {
            handleSuccess(form);
          } else {
            return res.json().then(function (body) {
              handleError(form, body);
            });
          }
        }).catch(function () {
          // Retry once
          sendLead(payload).catch(noop);
        });
      } else {
        // Non-blocking: fire and forget with keepalive
        sendLead(payload, true).catch(function () {
          sendLead(payload, true).catch(noop);
        });
      }
    });
  }

  // ── Render hosted form ──────────────────────────────────────────────
  function renderHostedForm() {
    var container = document.querySelector(
      script.getAttribute('data-sankore-container') || '[data-sankore-form-container]'
    );

    if (!container) {
      // Create a container after the script tag
      container = document.createElement('div');
      script.parentNode.insertBefore(container, script.nextSibling);
    }

    // Fetch form definition
    fetch(apiBase + '/api/ingest/web/' + publicKey + '/form', {
      mode: 'cors'
    }).then(function (res) {
      if (!res.ok) throw new Error('Form not found');
      return res.json();
    }).then(function (def) {
      buildHostedForm(container, def);
    }).catch(function (err) {
      console.warn('[Sankore] Failed to load form definition:', err.message);
    });
  }

  function buildHostedForm(container, def) {
    // Use Shadow DOM for style isolation
    var shadow = container.attachShadow
      ? container.attachShadow({ mode: 'open' })
      : container; // fallback for older browsers

    var style = document.createElement('style');
    style.textContent = getFormStyles(def.theme);
    shadow.appendChild(style);

    var form = document.createElement('form');
    form.setAttribute('novalidate', '');
    form.className = 'sankore-form';

    var formLoadedAt = new Date().toISOString();

    // Fields
    (def.fields || []).forEach(function (field) {
      var wrapper = document.createElement('div');
      wrapper.className = 'sankore-field';

      var label = document.createElement('label');
      label.setAttribute('for', 'sf-' + field.name);
      label.textContent = field.label;
      if (field.isRequired) {
        var req = document.createElement('span');
        req.className = 'sankore-required';
        req.textContent = ' *';
        label.appendChild(req);
      }
      wrapper.appendChild(label);

      var input;
      if (field.type === 'textarea') {
        input = document.createElement('textarea');
      } else if (field.type === 'select' && field.options) {
        input = document.createElement('select');
        var emptyOpt = document.createElement('option');
        emptyOpt.value = '';
        emptyOpt.textContent = '-- ' + field.label + ' --';
        input.appendChild(emptyOpt);
        field.options.forEach(function (opt) {
          var o = document.createElement('option');
          o.value = opt;
          o.textContent = opt;
          input.appendChild(o);
        });
      } else {
        input = document.createElement('input');
        input.type = field.type || 'text';
      }

      input.id = 'sf-' + field.name;
      input.name = field.name;
      if (field.isRequired) input.required = true;
      if (field.placeholder) input.placeholder = field.placeholder;
      wrapper.appendChild(input);

      form.appendChild(wrapper);
    });

    // Consent checkbox
    if (def.consentText) {
      var consentWrapper = document.createElement('div');
      consentWrapper.className = 'sankore-field sankore-consent';

      var cbLabel = document.createElement('label');
      var cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.name = '_consent';
      cb.required = true;
      cbLabel.appendChild(cb);
      var consentSpan = document.createElement('span');
      consentSpan.innerHTML = ' ' + def.consentText;
      cbLabel.appendChild(consentSpan);
      consentWrapper.appendChild(cbLabel);
      form.appendChild(consentWrapper);
    }

    // Submit button
    var btnWrapper = document.createElement('div');
    btnWrapper.className = 'sankore-field';
    var btn = document.createElement('button');
    btn.type = 'submit';
    btn.textContent = def.submitLabel || 'Submit';
    btnWrapper.appendChild(btn);
    form.appendChild(btnWrapper);

    // Message area (aria-live for screen readers)
    var msgArea = document.createElement('div');
    msgArea.className = 'sankore-message';
    msgArea.setAttribute('aria-live', 'polite');
    msgArea.setAttribute('role', 'status');
    form.appendChild(msgArea);

    shadow.appendChild(form);

    // Handle submission
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      btn.disabled = true;
      btn.textContent = '...';

      var fields = collectFields(form);
      var consent = form.querySelector('[name="_consent"]');
      var consentChecked = consent ? consent.checked : false;
      var payload = buildPayload(fields, consentChecked, null, formLoadedAt);
      payload.consentVersion = def.consentVersion || null;

      sendLead(payload).then(function (res) {
        if (res.ok || res.status === 202) {
          msgArea.textContent = '✓ Merci ! Votre demande a été envoyée.';
          msgArea.className = 'sankore-message sankore-success';
          form.reset();
        } else {
          return res.json().then(function (body) {
            msgArea.textContent = body.error || 'Une erreur est survenue.';
            msgArea.className = 'sankore-message sankore-error';
          });
        }
      }).catch(function () {
        msgArea.textContent = 'Erreur réseau. Veuillez réessayer.';
        msgArea.className = 'sankore-message sankore-error';
      }).finally(function () {
        btn.disabled = false;
        btn.textContent = def.submitLabel || 'Submit';
      });
    });
  }

  // ── Field collection ────────────────────────────────────────────────
  function collectFields(form) {
    var fields = {};
    var elements = form.elements;
    for (var i = 0; i < elements.length; i++) {
      var el = elements[i];
      if (!el.name || el.name.startsWith('_') || el.type === 'submit' || el.type === 'button') continue;
      if (el.type === 'checkbox') {
        fields[el.name] = el.checked ? 'true' : 'false';
      } else if (el.type === 'radio') {
        if (el.checked) fields[el.name] = el.value;
      } else {
        fields[el.name] = el.value;
      }
    }
    return fields;
  }

  function getConsent(form) {
    var el = form.querySelector('[name="consent"], [name="_consent"], [data-sankore-consent]');
    return el ? el.checked : true;
  }

  function getCaptchaToken(form) {
    // Turnstile
    var turnstile = form.querySelector('[name="cf-turnstile-response"]');
    if (turnstile && turnstile.value) return turnstile.value;

    // hCaptcha
    var hcaptcha = form.querySelector('[name="h-captcha-response"]');
    if (hcaptcha && hcaptcha.value) return hcaptcha.value;

    // reCAPTCHA v3 (global)
    if (window.grecaptcha && window.grecaptcha.getResponse) {
      var token = window.grecaptcha.getResponse();
      if (token) return token;
    }

    return null;
  }

  // ── Payload builder ─────────────────────────────────────────────────
  function buildPayload(fields, consent, captchaToken, formLoadedAt) {
    var utm = getStoredUtm();

    return {
      fields: fields,
      consent: consent,
      consentVersion: null,
      captchaToken: captchaToken,
      formLoadedAt: formLoadedAt,
      page: window.location.href,
      referrer: document.referrer || null,
      utmSource: utm.utm_source || null,
      utmMedium: utm.utm_medium || null,
      utmCampaign: utm.utm_campaign || null,
      utmContent: utm.utm_content || null,
      utmTerm: utm.utm_term || null,
      sdkVersion: SDK_VERSION
    };
  }

  // ── Send lead ───────────────────────────────────────────────────────
  function sendLead(payload, keepalive) {
    return fetch(apiBase + '/api/ingest/web/' + publicKey, {
      method: 'POST',
      mode: 'cors',
      keepalive: !!keepalive,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
  }

  // ── Success / Error handling ────────────────────────────────────────
  function handleSuccess(form) {
    var redirect = script.getAttribute('data-sankore-redirect');
    if (redirect) {
      window.location.href = redirect;
      return;
    }

    var msg = script.getAttribute('data-sankore-success') || 'Merci ! Votre demande a été envoyée.';
    var div = document.createElement('div');
    div.setAttribute('aria-live', 'polite');
    div.setAttribute('role', 'status');
    div.className = 'sankore-success-message';
    div.textContent = msg;
    form.parentNode.replaceChild(div, form);
  }

  function handleError(form, body) {
    var existing = form.querySelector('.sankore-error-message');
    if (existing) existing.remove();

    var msg = (body && body.error) || 'Une erreur est survenue. Veuillez réessayer.';
    var div = document.createElement('div');
    div.className = 'sankore-error-message';
    div.setAttribute('role', 'alert');
    div.textContent = msg;
    form.insertBefore(div, form.firstChild);
  }

  // ── UTM tracking ────────────────────────────────────────────────────
  function captureUtm() {
    if (sessionStorage.getItem(UTM_KEY)) return;

    var params = new URLSearchParams(window.location.search);
    var utm = {};
    ['utm_source', 'utm_medium', 'utm_campaign', 'utm_content', 'utm_term'].forEach(function (key) {
      var val = params.get(key);
      if (val) utm[key] = val;
    });

    if (Object.keys(utm).length > 0) {
      sessionStorage.setItem(UTM_KEY, JSON.stringify(utm));
    }
  }

  function getStoredUtm() {
    try {
      var stored = sessionStorage.getItem(UTM_KEY);
      return stored ? JSON.parse(stored) : {};
    } catch (e) {
      return {};
    }
  }

  // ── Hosted form styles ──────────────────────────────────────────────
  function getFormStyles(theme) {
    var primary = 'var(--sankore-primary, #2563eb)';
    var bg = theme === 'dark' ? '#1f2937' : '#ffffff';
    var text = theme === 'dark' ? '#f9fafb' : '#111827';
    var border = theme === 'dark' ? '#374151' : '#d1d5db';

    return [
      '.sankore-form { font-family: var(--sankore-font, system-ui, sans-serif); max-width: 480px; margin: 0 auto; padding: 1.5rem; background: ' + bg + '; color: ' + text + '; border-radius: 8px; }',
      '.sankore-field { margin-bottom: 1rem; }',
      '.sankore-field label { display: block; margin-bottom: 0.25rem; font-weight: 500; font-size: 0.875rem; }',
      '.sankore-field input, .sankore-field select, .sankore-field textarea { width: 100%; padding: 0.5rem 0.75rem; border: 1px solid ' + border + '; border-radius: 6px; font-size: 1rem; box-sizing: border-box; background: ' + bg + '; color: ' + text + '; }',
      '.sankore-field input:focus, .sankore-field select:focus, .sankore-field textarea:focus { outline: 2px solid ' + primary + '; outline-offset: 1px; border-color: ' + primary + '; }',
      '.sankore-field textarea { min-height: 80px; resize: vertical; }',
      '.sankore-consent label { display: flex; align-items: flex-start; gap: 0.5rem; font-size: 0.8125rem; cursor: pointer; }',
      '.sankore-consent input[type="checkbox"] { width: auto; margin-top: 0.2rem; }',
      '.sankore-required { color: #ef4444; }',
      '.sankore-field button { width: 100%; padding: 0.625rem 1rem; background: ' + primary + '; color: #fff; border: none; border-radius: 6px; font-size: 1rem; font-weight: 600; cursor: pointer; }',
      '.sankore-field button:hover { opacity: 0.9; }',
      '.sankore-field button:disabled { opacity: 0.5; cursor: not-allowed; }',
      '.sankore-field button:focus-visible { outline: 2px solid ' + primary + '; outline-offset: 2px; }',
      '.sankore-message { margin-top: 0.75rem; padding: 0.75rem; border-radius: 6px; font-size: 0.875rem; }',
      '.sankore-success { background: #d1fae5; color: #065f46; }',
      '.sankore-error { background: #fee2e2; color: #991b1b; }',
      '.sankore-error-message { background: #fee2e2; color: #991b1b; padding: 0.75rem; border-radius: 6px; margin-bottom: 1rem; font-size: 0.875rem; }',
      '.sankore-success-message { text-align: center; padding: 2rem; font-size: 1.125rem; color: #065f46; }'
    ].join('\n');
  }

  function noop() {}

})();
