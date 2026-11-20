// The status panels (reactor, materials, upgrades), the hit flash and the death notice, from the frame after each
// step. The HUD updates 24 times a second, so everything here writes to the page only what changed: a write of the
// same text or style still makes the browser recompute style and layout.

const $ = (id) => document.getElementById(id);

export function setText(element, text) {
    if (element.textContent !== text) element.textContent = text;
}

export function setClass(element, name) {
    if (element.className !== name) element.className = name;
}

export function setHidden(element, hidden) {
    if (element.hidden !== hidden) element.hidden = hidden;
}

export function setProperty(element, name, value) {
    if (element.style.getPropertyValue(name) !== value)
        element.style.setProperty(name, value);
}

export function setTitle(element, title) {
    if (element.title !== title) element.title = title;
}

// A meter moves in steps of 1/400, finer than its pixels, so a steady value writes nothing.
function setMeter(meter, value) {
    const v = Math.round(value * 400) / 400;
    if (meter.value !== v) meter.value = v;
}

const BUILD_KEYS = ["E", "R", "T", "Y", "U"];

export class StatusView {
    constructor(input) {
        this.input = input;
        $("recall").onclick = () => input.queue("recall", true);
        this.tankStatus = document.querySelector(".tank-status");
        this.builds = null;
        this.upgrades = null;
    }

    update(f) {
        const p = f.player;
        setText($("temperature"), `${Math.round(p.heat)}°`);
        setProperty(this.tankStatus, "--heat-color", p.heatColor);
        setMeter($("heat"), p.heatGauge);
        setMeter($("shield"), p.shield);
        setText($("shield-text"), `${Math.round(p.shield * 100)}%`);
        setText(
            $("network"),
            p.docked
                ? "DOCKED"
                : p.offNetwork
                  ? `OFF GRID / ${Math.ceil(p.safeSeconds)}s`
                  : "ON NETWORK",
        );
        setClass($("network"), p.offNetwork ? "tone-Warn" : "tone-Info");
        setText(
            $("position"),
            `X ${p.position.x} / Y ${p.position.y} · WORLD ${f.seed}`,
        );
        setText($("ore"), `${p.ore}/${p.holdCapacity} ORE · BASE ${p.store}`);
        const pile = $("ore-pile");
        setHidden(pile, p.orePileFrame < 0);
        if (p.orePileFrame >= 0) {
            const column = p.orePileFrame % 8, row = Math.floor(p.orePileFrame / 8);
            setProperty(pile, "background-position", `${(column / 7) * 100}% ${(row / 4) * 100}%`);
        }
        setText(
            $("power"),
            `BASE ARMOR ${Math.round(p.baseArmor * 100)}% / CHARGE ${Math.round(p.charge * 100)}% / HEAT ${Math.round(p.baseHeat * 100)}%`,
        );
        // With the energy network: the base store, what its engine and generators make against what its island draws, and the net.
        const store = f.basePower;
        setHidden($("network-power"), !store);
        if (store) {
            setText($("network-power"), `NETWORK ${store.store} ${store.flow} ${store.net} · ${store.shield}`);
            setProperty($("network-power"), "color", store.color);
        }
        this.updateBuilds(f.builds, p);
        setHidden($("upgrades"), !p.docked || p.dead);
        this.updateUpgrades(f.upgrades, p);
        this.updateMachineUpgrade(f.machineUpgrade);
        this.updateHit(p);
        setHidden($("recall"), !p.canRecall || p.outcome !== "InProgress");
        setHidden($("death"), !p.dead && p.outcome === "InProgress");
        setText(
            $("death"),
            p.outcome === "Victory"
                ? "SECTOR SECURED\nOpen the menu for a new world."
                : p.outcome === "Defeat"
                  ? p.baseDestroyed
                      ? "BASE DESTROYED\nOpen the menu to try a new world."
                      : "DIVE ENDED\nOpen the menu to try a new world."
                  : `TANK LOST\nRespawning in ${Math.ceil(p.respawnSeconds)}s`,
        );
    }

    updateBuilds(builds, p) {
        if (!this.builds || this.builds.size !== builds.length) {
            this.builds = new Map();
            $("builds").replaceChildren();
            for (const b of builds) {
                const button = document.createElement("button");
                button.onclick = () => this.input.queue("build", b.key);
                const key = document.createElement("kbd");
                key.textContent = BUILD_KEYS[b.key - 1];
                const name = document.createElement("span");
                name.textContent = b.name;
                const cost = document.createElement("small");
                button.append(key, name, cost);
                $("builds").append(button);
                this.builds.set(b.key, { button, cost });
            }
        }
        for (const b of builds) {
            const { button, cost } = this.builds.get(b.key);
            setText(cost, `${b.cost} ORE`);
            setTitle(
                button,
                p.carrying
                    ? `Plant carried ${p.carrying}`
                    : `Build ${b.name}: ${b.cost} ore (${b.payer})`,
            );
            setClass(button, b.affordable || p.carrying ? "" : "unaffordable");
        }
    }

    // The base track above the tank track, as on the desktop: key, name, level pips, cost, and what it does.
    updateUpgrades(upgrades, p) {
        if (!this.upgrades) {
            this.upgrades = new Map();
            let track = null;
            for (const u of upgrades) {
                if (u.track !== track) {
                    track = u.track;
                    const heading = document.createElement("h3");
                    heading.textContent = track.toUpperCase();
                    $("upgrade-list").append(heading);
                }
                const button = document.createElement("button");
                button.onclick = () => this.input.queue("upgrade", u.key);
                button.title = u.effect;
                const key = document.createElement("kbd");
                key.textContent = String(u.key);
                const name = document.createElement("span");
                name.className = "name";
                name.textContent = u.name;
                const pips = document.createElement("span");
                pips.className = "pips";
                for (let i = 0; i < u.maxLevel; i++)
                    pips.append(document.createElement("i"));
                const cost = document.createElement("small");
                const effect = document.createElement("span");
                effect.className = "effect";
                effect.textContent = u.effect;
                button.append(key, name, pips, cost, effect);
                $("upgrade-list").append(button);
                this.upgrades.set(u.key, { button, pips, cost });
            }
        }
        for (const u of upgrades) {
            const { button, pips, cost } = this.upgrades.get(u.key);
            [...pips.children].forEach((pip, i) =>
                setClass(pip, i < u.level ? "on" : ""),
            );
            setText(cost, u.maxed ? "MAX" : `${u.cost} ore`);
            const disabled = u.maxed || p.store < u.cost;
            if (button.disabled !== disabled) button.disabled = disabled;
        }
    }

    // The upgrade panel of the machine next to the tank (MachineUpgradeView, as on the desktop): a row per path with
    // its key, name, price and effect. renderer.js keeps it beside the machine.
    updateMachineUpgrade(m) {
        const panel = $("machine-upgrade");
        setHidden(panel, !m?.open);
        if (!m?.open) return;
        setText($("machine-upgrade-title"), `${m.title} · LEVEL ${m.level}/${m.maxLevel}`);
        const list = $("machine-upgrade-list");
        if (!this.machineChoices || this.machineChoices.length !== m.choices.length) {
            list.replaceChildren();
            this.machineChoices = m.choices.map((choice) => {
                const button = document.createElement("button");
                button.onclick = () => this.input.queue("upgrade", choice.key);
                const key = document.createElement("kbd");
                key.textContent = String(choice.key);
                const name = document.createElement("span");
                name.className = "name";
                const cost = document.createElement("small");
                const effect = document.createElement("span");
                effect.className = "effect";
                button.append(key, name, cost, effect);
                list.append(button);
                return { button, name, cost, effect };
            });
            $("machine-upgrade-close").onclick = () => this.input.queue("machineUpgrade", true);
        }
        m.choices.forEach((choice, i) => {
            const row = this.machineChoices[i];
            setText(row.name, choice.name);
            setProperty(row.name, "color", choice.nameColor);
            setText(row.cost, choice.price);
            setProperty(row.cost, "color", choice.priceColor);
            setText(row.effect, choice.effect);
            if (row.button.disabled !== !choice.canBuy) row.button.disabled = !choice.canBuy;
        });
        setText($("machine-upgrade-foot"), `BASE ORE STORE ${m.store}`);
        setHidden($("machine-upgrade-refusal"), !m.refusal);
        setText($("machine-upgrade-refusal"), m.refusal || "");
    }

    // The red flash after a hit, and the wedge pointing the way the shot came from.
    updateHit(p) {
        const hit = $("hit");
        setHidden(hit, !(p.hitFlash > 0));
        if (!(p.hitFlash > 0)) return;
        setProperty(hit, "--hit", p.hitFlash.toFixed(2));
        const wedge = $("hit-wedge");
        const known = p.hitFromX !== 0 || p.hitFromY !== 0;
        setHidden(wedge, !known);
        if (known)
            setProperty(
                wedge,
                "--angle",
                `${Math.atan2(p.hitFromX, -p.hitFromY).toFixed(3)}rad`,
            );
    }
}
