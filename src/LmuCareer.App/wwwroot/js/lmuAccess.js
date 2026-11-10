import { call, onEvent } from "./api.js";
import { h } from "./dom.js";
import { showModal, closeModal, attempt } from "./app.js";
import { t } from "./i18n.js";

// Using LMU's local interface (setting races up, reading its car list and DLC ownership) needs the
// player's say-so first: a notice, an unticked box, and Allow. It lasts until the app closes, and
// Settings can take it back. The app checks it too, so no call reaches LMU without it. Setting a
// race up by hand never needs it. Credit: thebaz's windows-weekend branch.

export const LMU_CONSENT_VERSION = "lmu-local-api-1";

let asking = null;

/** Resolves true once the player has allowed LMU access this run of the app; asks when they haven't. */
export async function ensureLmuConsent() {
  const status = await call("lmuConsent").catch(() => null);
  if (status?.granted) return true;
  asking ??= askForConsent().finally(() => { asking = null; });
  return asking;
}

export async function revokeLmuConsent() {
  return attempt(call("revokeLmuConsent"));
}

function askForConsent() {
  return new Promise((resolve) => {
    let answered = false;
    const done = (yes) => { if (answered) return; answered = true; closeModal(); resolve(yes); };
    const allow = h("button", { class: "btn", disabled: true, "data-sound": "confirm",
      onclick: async () => {
        if (!box.checked) return;
        done(!!(await attempt(call("grantLmuConsent", { version: LMU_CONSENT_VERSION }))));
      } }, t("Allow for this session"));
    const box = h("input", { type: "checkbox", onchange: () => { allow.disabled = !box.checked; } });
    showModal([
      h("h2", {}, t("Use LMU's local interface?")),
      h("p", {}, t("To set races up for you, read your DLC and show car liveries, Factory Seat talks to LMU through the local interface its own menus use, on this PC only (localhost). It never contacts Studio 397's servers or uses your login.")),
      h("p", {}, t("That interface isn't public: an LMU update can change it, and a request that fails can leave some of LMU's race settings changed. Check them in the game before you race.")),
      h("p", {}, t("The original Factory Seat author doesn't develop, maintain or support this fork's LMU integration. It comes with no guarantee: use it at your own risk.")),
      h("p", { class: "muted" }, t("Setting races up by hand and reading results don't need any of this.")),
      h("label", { class: "row", style: { gap: "10px", alignItems: "flex-start", margin: "12px 0" } }, box,
        h("span", {}, t("I've read this and allow Factory Seat to use LMU's local interface until I close the app."))),
      h("div", { class: "modal-actions" },
        h("button", { class: "btn ghost", "data-sound": "back", onclick: () => done(false) }, t("Not now")), allow),
    ]);
    // Closing the dialog any other way (Escape, a click outside) allows nothing.
    const layer = document.querySelector(".modal-layer");
    const watch = new MutationObserver(() => { if (layer?.hidden) { watch.disconnect(); done(false); } });
    if (layer) watch.observe(layer, { attributes: true, attributeFilter: ["hidden"] });
  });
}

/**
 * Asks LMU which packs the player owns. Resolves { owned, notOwned, unsure } (pack ids), or null
 * when the player didn't allow it or LMU couldn't answer (an error toast says why).
 */
export async function detectOwnedPacks() {
  if (!(await ensureLmuConsent())) return null;
  const answer = new Promise((resolve) => {
    const stop = onEvent("ownedPacks", (message) => { stop(); resolve(message); });
  });
  if (!(await attempt(call("detectOwnedPacks")))) return null;
  const message = await answer;
  if (message.status !== "done") {
    await attempt(Promise.reject(Object.assign(new Error(message.error), { phrase: message.phrase })));
    return null;
  }
  return { owned: message.owned ?? [], notOwned: message.notOwned ?? [], unsure: message.unsure ?? [] };
}
