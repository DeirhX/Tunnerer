// The desktop builds the welcome screen offers (index.html, the Desktop section). The markup lists every platform, in
// the order written, and a platform with a published build has a link in its row; without JavaScript that list is
// the whole feature. This module works out which platform the visitor is on. On a computer it shows only that
// platform's row, and a button expands the other platforms. On a phone, or where the platform is unknown, the whole
// list stays. It never adds a link: the URLs live in index.html only.

const NAMES = { windows: "Windows", macos: "macOS", linux: "Linux" };

// The platform a browser is on: 'windows', 'macos', 'linux', 'mobile' (a phone or tablet, which has no desktop build)
// or 'unknown'. It takes the navigator's fields, so tools/browser/downloads.test.mjs can run it on each platform's
// strings. The client hints (navigator.userAgentData) decide where the browser has them. Elsewhere the user agent and
// platform strings do. An iPad in desktop mode reports a Mac, and only its touch points give it away.
export function detectPlatform(nav) {
    const ua = nav.userAgent ?? "";
    const platform = nav.platform ?? "";
    const hints = nav.userAgentData;
    if (hints?.mobile || hints?.platform === "Android" || hints?.platform === "iOS") return "mobile";
    const named = { Windows: "windows", macOS: "macos", Linux: "linux" }[hints?.platform];
    if (named) return named;
    if (/Android|iPhone|iPod|iPad/.test(ua)) return "mobile";
    if (/^Mac/.test(platform) && (nav.maxTouchPoints ?? 0) > 1) return "mobile";
    // Chrome OS has no build; its user agent names X11 and Linux too, so it is checked first.
    if (/CrOS/.test(ua)) return "unknown";
    if (/Windows/.test(ua) || /^Win/.test(platform)) return "windows";
    // Apple silicon Macs report the same Intel string as Intel ones, so one macOS build is offered for both.
    if (/Macintosh|Mac OS X/.test(ua) || /^Mac/.test(platform)) return "macos";
    if (/Linux|X11/.test(ua) || /Linux/.test(platform)) return "linux";
    return "unknown";
}

// Narrows the Desktop section to the visitor's platform: its row is the only one shown, styled as the main download,
// and the lead line says whether a build exists for it. A button under the list shows the other platforms' rows,
// and hides them again. A phone, or a platform the page does not know, keeps the whole list and the lead as written.
export function offerDownloads(doc, platform) {
    const list = doc.querySelector(".download-list");
    const lead = doc.getElementById("downloads-lead");
    if (!list || !lead) return;
    if (platform === "mobile") {
        lead.textContent = "Desktop builds are for computers. On a phone or tablet, play the browser edition on this page.";
        return;
    }
    const name = NAMES[platform];
    if (!name) return;
    const row = [...list.children].find((item) => item.dataset.os === platform);
    if (!row) return;
    const published = Boolean(row.querySelector("a.download-link"));
    lead.textContent = published
        ? `Your computer looks like ${name}, so its build is offered here.`
        : `No ${name} build is published yet.`;
    list.prepend(row);
    row.classList.add("is-suggested");
    const others = [...list.children].filter((item) => item !== row);
    for (const item of others) item.hidden = true;
    const toggle = doc.createElement("button");
    toggle.type = "button";
    toggle.className = "download-more";
    toggle.setAttribute("aria-expanded", "false");
    const closedLabel = `Other platforms (${others.length})`;
    toggle.textContent = closedLabel;
    toggle.addEventListener("click", () => {
        const open = toggle.getAttribute("aria-expanded") !== "true";
        toggle.setAttribute("aria-expanded", String(open));
        toggle.textContent = open ? "Hide other platforms" : closedLabel;
        for (const item of others) item.hidden = !open;
    });
    list.after(toggle);
}
