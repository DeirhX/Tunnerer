// The desktop's bottom HUD (HudStrip.cs and the gauges it draws): the TEMP, SHIELD and MATERIALS art in the
// bottom-left corner, BUILD STRUCTURES and COMMAND in the bottom-right, the middle of the view left clear. It draws
// the desktop's own art (copied in by tools/browser/publish.py) on two canvases, at the desktop's art coordinates,
// so every number below is the desktop's. Shown on wide screens with a mouse; phones keep the plates (hud.js).
//
// It is drawn after each step, 24 times a second, and not while the game is paused. The desktop eases its gauges
// once a display frame; here they ease once a step, by as much as the desktop's do over the frames of a step.

// HudLayout.cs: the art's own size, and where each panel starts along it.
const STRIP_H = 223,
    HEAT_W = 245,
    SHIELD_W = 145,
    MATERIALS_W = 230,
    BUILD_W = 404;
const HEAT_X = 0,
    SHIELD_X = HEAT_X + HEAT_W,
    MATERIALS_X = SHIELD_X + SHIELD_W,
    BUILD_X = MATERIALS_X + MATERIALS_W;
const LEFT_W = BUILD_X,
    RIGHT_W = BUILD_W,
    MIN_CENTER_GAP = 404,
    REFERENCE_HEIGHT = 1080,
    // Texture pixels per art pixel: the PNGs are drawn at twice their layout size.
    ART_DENSITY = 2;
// Room above the left part for the OFF NETWORK tag, which the desktop draws 24 art pixels above TEMP.
const HEADROOM = 44;

// HudStrip.cs: the plates' centres, title rows and readout rows.
const TEMP = { x: HEAT_X + 170, title: 151.5, readout: 179, right: HEAT_X + 239 },
    SHIELD = { x: SHIELD_X + 80.5, title: 153.5, readout: 180 },
    MATERIALS = { x: MATERIALS_X + 120, title: 154, readout: 184 };
const ORE_LINE = { x: MATERIALS_X + 126, y: 84, size: 19 };

// HeatGauge.cs
const ORB = { x: HEAT_X + 168, y: 72, r: 47 };
const DIAL_START = (Math.PI * 150) / 180,
    DIAL_SWEEP = (Math.PI * 240) / 180;

// LiquidGauges.cs: the shield crystal and the open top of the chest, in their panels.
const SHIELD_OUTLINE = [
    [44, 44], [60, 40], [82, 37], [104, 40], [121, 44], [121, 80],
    [114, 100], [100, 116], [82, 128], [64, 116], [50, 100], [44, 80],
].map(([x, y]) => [SHIELD_X + x, y]);
const CHEST_OUTLINE = [
    [52, 40], [193, 40], [206, 52], [206, 86], [185, 98], [60, 98], [40, 86], [40, 52],
].map(([x, y]) => [MATERIALS_X + x, y]);

// BuildPanel.cs: the slots inside build_panel.png, three columns by two rows.
const SLOT_X = [25, 117, 209],
    SLOT_Y = [44, 105],
    SLOT_W = 80,
    SLOT_H = 56;
const COMMAND = { x0: BUILD_X + 77, y0: 172, x1: BUILD_X + 277, y1: 212 };

// The desktop's per-frame easing (at 60 frames a second) over one step (24 a second).
const perStep = (k) => 1 - Math.pow(1 - k, 60 / 24);
const EASE_HEAT = perStep(0.15),
    EASE_SHIELD = perStep(0.2),
    EASE_CHEST = perStep(0.15);

const FONT = '"Chakra Petch", system-ui, sans-serif';
// ImGui sizes a font by its line's height (ascent to descent) and CSS by its em, so the desktop's sizes are scaled
// by the font's em over its line height once it has loaded.
let fontScale = 1;
function measureFontScale() {
    const ctx = document.createElement("canvas").getContext("2d");
    ctx.font = `500 100px ${FONT}`;
    const m = ctx.measureText("Hg");
    const line = m.fontBoundingBoxAscent + m.fontBoundingBoxDescent;
    if (line > 0) fontScale = 100 / line;
}
const font = (size) => `500 ${(size * fontScale).toFixed(2)}px ${FONT}`;
const ART = {
    heat: "hud/heat_icon.png",
    shield: "hud/shield_icon.png",
    materials: "hud/panel_frame.png",
    build: "hud/build_panel.png",
};

// An ImGui colour as the desktop writes it (0xAABBGGRR) in CSS.
function abgr(u) {
    const r = u & 0xff,
        g = (u >>> 8) & 0xff,
        b = (u >>> 16) & 0xff,
        a = (u >>> 24) / 255;
    return `rgba(${r},${g},${b},${a.toFixed(3)})`;
}

const BUILD_STATE_BORDER = {
    Ready: abgr(0xff40ff60),
    Filling: abgr(0xff707070),
    Blocked: abgr(0xff707070),
};
const CAPTION_COLOR = {
    FromBase: abgr(0xfff0d080),
    FromHold: abgr(0xffd8e860),
    KeyAndName: abgr(0xffb0b0b0),
};

// HudLayout.HudScale and ArtScale: art pixels to CSS pixels, enlarged above a 1080-high window, shrunk only when the
// window is too narrow for both parts and the gap between them, and snapped to whole device pixels where that keeps
// the art from being resampled.
export function stripScale(width, height, density) {
    const gap = Math.min(MIN_CENTER_GAP, width * 0.5);
    const fit = Math.max(0, width - gap) / (LEFT_W + RIGHT_W);
    const scale = Math.min(fit, Math.max(1, height / REFERENCE_HEIGHT));
    if (density <= 1) return scale;
    const exact = scale * density;
    return exact < ART_DENSITY ? scale : Math.floor(exact) / density;
}

export class StripView {
    constructor(input, onCommand) {
        this.left = document.getElementById("strip-left");
        this.right = document.getElementById("strip-right");
        this.input = input;
        this.art = {};
        this.loading = null;
        this.frame = null;
        this.dial = null;
        this.scale = 0;
        this.density = 0;
        this.reset();
        this.right.addEventListener("pointerdown", (e) => {
            const p = this.artPoint(this.right, e, BUILD_X, 0);
            if (!p) return;
            e.preventDefault();
            const col = SLOT_X.findIndex(
                (x) => p.x >= BUILD_X + x && p.x < BUILD_X + x + SLOT_W,
            );
            if (col >= 0 && p.y >= SLOT_Y[0] && p.y < SLOT_Y[0] + SLOT_H) {
                const build = this.frame?.builds.find((b) => b.slot === col);
                if (build) input.queue("build", build.key);
            } else if (
                p.x >= COMMAND.x0 && p.x < COMMAND.x1 &&
                p.y >= COMMAND.y0 && p.y < COMMAND.y1
            )
                onCommand();
        });
        // The strip is part of the HUD: a click on it works the HUD and does not fire.
        this.left.addEventListener("pointerdown", (e) => e.preventDefault());
    }

    // Forgets what the last match showed: the next frame starts the gauges where they stand.
    reset() {
        this.shown = { heat: NaN, shield: -1, chest: -1 };
        this.lastHeat = NaN;
        this.trend = 0;
    }

    // Loads the art once, the first time the strip is shown.
    load() {
        this.loading ||= Promise.all(
            Object.entries(ART).map(
                ([name, src]) =>
                    new Promise((resolve, reject) => {
                        const image = new Image();
                        image.onload = () => resolve([name, image]);
                        image.onerror = () => reject(new Error(`HUD art ${src} did not load`));
                        image.src = src;
                    }),
            ),
        ).then(async (images) => {
            this.art = Object.fromEntries(images);
            await document.fonts.load(`20px ${FONT}`);
            measureFontScale();
            if (this.frame) this.draw(this.frame, this.time);
        });
        this.loading.catch((error) => console.warn(error));
    }

    // Where a pointer event lies in art pixels, for a canvas whose left edge is at art x `originX`.
    artPoint(canvas, e, originX, top) {
        if (!this.scale) return null;
        const r = canvas.getBoundingClientRect();
        return {
            x: originX + (e.clientX - r.left) / this.scale,
            y: (e.clientY - r.top) / this.scale - top,
        };
    }

    // Sizes the two canvases for the window. Returns the strip's height in CSS pixels.
    layout(width, height) {
        const density = Math.min(window.devicePixelRatio || 1, 3);
        const scale = stripScale(width, height, density);
        if (scale !== this.scale || density !== this.density) {
            this.scale = scale;
            this.density = density;
            size(this.left, LEFT_W * scale, (STRIP_H + HEADROOM) * scale, density);
            size(this.right, RIGHT_W * scale, STRIP_H * scale, density);
            if (this.frame) this.draw(this.frame, this.time);
        }
        return STRIP_H * scale;
    }

    update(f, time) {
        if (f.dial) this.dial = f.dial;
        const p = f.player;
        // A dead tank's orb sinks to the cold end, as on the desktop.
        const heat = p.dead && this.dial ? this.dial.min : p.heat;
        this.shown.heat = Number.isNaN(this.shown.heat)
            ? heat
            : this.shown.heat + (heat - this.shown.heat) * EASE_HEAT;
        this.shown.shield =
            this.shown.shield < 0
                ? p.shield
                : this.shown.shield + (p.shield - this.shown.shield) * EASE_SHIELD;
        this.shown.chest =
            this.shown.chest < 0
                ? p.holdFill
                : this.shown.chest + (p.holdFill - this.shown.chest) * EASE_CHEST;
        // The trend arrow: the desktop smooths the change per display frame and shows it past 0.02 a frame.
        if (!Number.isNaN(this.lastHeat) && !p.dead)
            this.trend = this.trend * 0.9 + ((p.heat - this.lastHeat) * 24) / 60 * 0.1;
        this.lastHeat = p.heat;
        this.frame = f;
        this.time = time;
        this.draw(f, time);
    }

    draw(f, time) {
        if (!this.art.build || !this.scale || !this.dial) return;
        const p = f.player;
        const k = this.scale * this.density;
        const left = this.left.getContext("2d");
        left.setTransform(1, 0, 0, 1, 0, 0);
        left.clearRect(0, 0, this.left.width, this.left.height);
        left.setTransform(k, 0, 0, k, 0, HEADROOM * k);
        const right = this.right.getContext("2d");
        right.setTransform(1, 0, 0, 1, 0, 0);
        right.clearRect(0, 0, this.right.width, this.right.height);
        right.setTransform(k, 0, 0, k, -BUILD_X * k, 0);

        for (const [ctx, image, x, w] of [
            [left, this.art.heat, HEAT_X, HEAT_W],
            [left, this.art.shield, SHIELD_X, SHIELD_W],
            [left, this.art.materials, MATERIALS_X, MATERIALS_W],
            [right, this.art.build, BUILD_X, BUILD_W],
        ])
            ctx.drawImage(image, x, 0, w, STRIP_H);

        stripText(left, "TEMP", TEMP.x, TEMP.title, 19, abgr(0xff78c8ff), abgr(0x181880ff));
        stripText(left, "SHIELD", SHIELD.x, SHIELD.title, 20, abgr(0xfff8e890), abgr(0x20ffa030));
        stripText(left, "MATERIALS", MATERIALS.x, MATERIALS.title, 21, abgr(0xff68e0ff), abgr(0x282090ff));
        stripText(right, "BUILD STRUCTURES", BUILD_X + 162, 23.5, 17, abgr(0xffb0e4f8), abgr(0x181880ff));
        stripText(right, "COMMAND", BUILD_X + 171, 191.5, 21, abgr(0xff98e8ff), abgr(0x202080ff));

        this.drawOrb(left, p, time);
        this.drawLiquids(left, p, time);

        const readout = (text, plate) =>
            stripText(left, text, plate.x, plate.readout, 22, abgr(0xffaafacd), abgr(0x2830ff60));
        readout(`${p.dead ? 0 : Math.round(p.heat)}°`, TEMP);
        this.drawTrend(left, p);
        readout(String(p.health), SHIELD);
        readout(`${p.ore} / ${p.holdCapacity}`, MATERIALS);

        const oreColor = p.unloading ? 0xff60ff90 : p.holdFull ? 0xff40c0ff : 0xffd8e860;
        stripText(left, p.oreLine, ORE_LINE.x, ORE_LINE.y, ORE_LINE.size, abgr(oreColor), abgr(0x28806020));
        if (p.unloading) {
            // Down arrows either side of the word, bobbing: the ore is going down into the base.
            const bob = ((time * 3) % 1) * 5;
            left.fillStyle = abgr(oreColor);
            for (const dx of [-72, 72]) {
                const cx = ORE_LINE.x + dx,
                    top = ORE_LINE.y - 8 + bob;
                triangle(left, cx - 7, top, cx + 7, top, cx, top + 9);
            }
        }
        this.drawOffNetwork(left, p, time);

        this.drawBuild(right, f.builds, p, time);
        // The COMMAND button stands for the base upgrades: lit while docked (keys 1-6 buy), dimmed elsewhere.
        if (!p.docked) {
            right.fillStyle = abgr(0x90000000);
            roundRect(right, COMMAND.x0, COMMAND.y0, COMMAND.x1, COMMAND.y1, 10);
            right.fill();
        } else {
            right.strokeStyle = abgr(Math.floor(time * 2) % 2 === 0 ? 0xff40ff60 : 0xff208030);
            right.lineWidth = 2.5;
            roundRect(right, COMMAND.x0, COMMAND.y0, COMMAND.x1, COMMAND.y1, 10);
            right.stroke();
        }
    }

    // The colour of the dial's band at a point of the heat scale (HeatScale.ColorRgb at the band's middle).
    bandColor(fraction) {
        const bands = this.dial.bands;
        return bands[Math.min(bands.length - 1, Math.max(0, Math.floor(fraction * bands.length)))];
    }

    // HeatGauge.DrawOrb: a glass sphere of liquid in the temperature's colour, frozen in the cold, bubbling as it
    // heats, boiling while overheating burns the shield; round it a dial lit up to the temperature, with a needle.
    // The desktop draws the liquid with a shader (HeatOrbPs); this is the same idea in canvas strokes.
    drawOrb(ctx, p, time) {
        const { x, y, r } = ORB;
        const alive = !p.dead;
        const dial = this.dial;
        // HeatScale.Fraction of the eased temperature.
        const lit = alive ? Math.min(1, Math.max(0, (this.shown.heat - dial.min) / (dial.max - dial.min))) : 0;
        const pulse = 0.5 + 0.5 * Math.sin(time * 10);

        // Dark glass behind everything (hides the energy bolt baked into the art), then the liquid.
        ctx.fillStyle = abgr(0xff0a0c0e);
        circle(ctx, x, y, r - 1);
        ctx.fill();
        const inner = r - 9;
        const liquid = alive ? this.bandColor(lit) : "#1f2124";
        const body = ctx.createRadialGradient(x - inner * 0.3, y - inner * 0.35, inner * 0.1, x, y, inner);
        body.addColorStop(0, mix(liquid, "#ffffff", 0.35));
        body.addColorStop(0.55, liquid);
        body.addColorStop(1, mix(liquid, "#000000", 0.6));
        ctx.fillStyle = body;
        circle(ctx, x, y, inner);
        ctx.fill();
        ctx.save();
        circle(ctx, x, y, inner);
        ctx.clip();
        if (alive && p.frozen > 0) {
            // Ice: a pale film and cracks that spread as it gets colder.
            ctx.fillStyle = `rgba(220,240,255,${(0.45 * p.frozen).toFixed(3)})`;
            ctx.fillRect(x - inner, y - inner, inner * 2, inner * 2);
            ctx.strokeStyle = `rgba(255,255,255,${(0.7 * p.frozen).toFixed(3)})`;
            ctx.lineWidth = 1.2;
            ctx.beginPath();
            for (let i = 0; i < 6; i++) {
                const a = i * 1.07 + 0.4;
                const len = inner * (0.35 + 0.6 * p.frozen) * (0.6 + 0.4 * ((i * 37) % 5) / 4);
                ctx.moveTo(x, y);
                ctx.lineTo(x + Math.cos(a) * len * 0.5 + 3, y + Math.sin(a) * len * 0.5);
                ctx.lineTo(x + Math.cos(a + 0.2) * len, y + Math.sin(a + 0.2) * len);
            }
            ctx.stroke();
        }
        if (alive && p.boil > 0) {
            // Bubbles rising, more and faster as it heats.
            const count = Math.round(4 + 14 * p.boil);
            ctx.fillStyle = "rgba(255,255,255,0.55)";
            for (let i = 0; i < count; i++) {
                const seed = (i * 0.618034) % 1;
                const rise = (time * (0.35 + 0.9 * p.boil) + seed * 7.3) % 1;
                const bx = x + (seed * 2 - 1) * inner * 0.75 + Math.sin(time * 3 + i) * 2;
                const by = y + inner - rise * inner * 2;
                circle(ctx, bx, by, 1 + 2.2 * p.boil * (1 - rise * 0.5));
                ctx.fill();
            }
        }
        if (alive && p.burning) {
            ctx.fillStyle = `rgba(255,60,30,${(0.2 + 0.25 * pulse).toFixed(3)})`;
            ctx.fillRect(x - inner, y - inner, inner * 2, inner * 2);
        }
        ctx.restore();

        // The dial round the rim: a dim track in the zone colours, lit up to the shown temperature.
        const arcR = r - 6,
            arcW = 5;
        ctx.lineWidth = arcW;
        const bands = dial.bands.length;
        for (let i = 0; i < bands; i++) {
            const f0 = i / bands,
                f1 = (i + 1) / bands;
            let a1 = DIAL_START + DIAL_SWEEP * f1 + 0.01;
            const on = alive && f0 < lit;
            if (on && f1 > lit) a1 = DIAL_START + DIAL_SWEEP * lit;
            ctx.strokeStyle = dial.bands[i] + (on ? "ff" : "48");
            ctx.beginPath();
            ctx.arc(x, y, arcR, DIAL_START + DIAL_SWEEP * f0, a1);
            ctx.stroke();
        }
        if (alive && p.burning) {
            ctx.strokeStyle = `rgba(255,77,51,${(0.35 + 0.5 * pulse).toFixed(3)})`;
            ctx.lineWidth = arcW + 3;
            ctx.beginPath();
            ctx.arc(x, y, arcR, DIAL_START + DIAL_SWEEP * dial.safe, DIAL_START + DIAL_SWEEP);
            ctx.stroke();
        }

        // Ticks at ambient and at the safe maximum, where overheating starts.
        for (const [t, color] of [[dial.ambient, "rgba(255,255,255,0.75)"], [dial.safe, abgr(0xff4060ff)]]) {
            const a = DIAL_START + DIAL_SWEEP * t;
            const dx = Math.cos(a),
                dy = Math.sin(a);
            line(ctx, x, y, dx, dy, arcR - arcW / 2 - 3, arcR + arcW / 2 + 1, "#000", 4);
            line(ctx, x, y, dx, dy, arcR - arcW / 2 - 2, arcR + arcW / 2, color, 2);
        }

        // The needle at the tip of the lit arc.
        if (alive) {
            const a = DIAL_START + DIAL_SWEEP * lit;
            const dx = Math.cos(a),
                dy = Math.sin(a);
            line(ctx, x, y, dx, dy, arcR - arcW / 2 - 5, arcR + arcW / 2 + 1, "#000", 4.5);
            line(ctx, x, y, dx, dy, arcR - arcW / 2 - 5, arcR + arcW / 2 + 1, "#fff", 2);
        }

        // A highlight on the glass.
        ctx.strokeStyle = abgr(0x30ffffff);
        ctx.lineWidth = 3;
        ctx.beginPath();
        ctx.arc(x, y, inner - 3, Math.PI * 1.15, Math.PI * 1.55);
        ctx.stroke();
    }

    // HeatGauge.DrawTrend: up (red) while digging or firing heats the tank, down (blue) while it cools.
    drawTrend(ctx, p) {
        if (p.dead || Math.abs(this.trend) < 0.02) return;
        const x = TEMP.right - 12,
            y = TEMP.readout,
            s = 6;
        if (this.trend > 0) {
            ctx.fillStyle = abgr(0xff4050ff);
            triangle(ctx, x - s, y + s * 0.6, x + s, y + s * 0.6, x, y - s * 0.8);
        } else {
            ctx.fillStyle = abgr(0xffffc050);
            triangle(ctx, x - s, y - s * 0.6, x + s, y - s * 0.6, x, y + s * 0.8);
        }
    }

    // HeatGauge.DrawOffNetworkTag: above TEMP while outside the network, how long until the heat floor reaches the
    // safe maximum.
    drawOffNetwork(ctx, p, time) {
        if (p.dead || !p.offNetwork) return;
        const text =
            p.safeSeconds > 0
                ? `OFF NETWORK  ~${Math.ceil(p.safeSeconds)}s`
                : "OFF NETWORK  OVERHEATING";
        const urgentBlink = p.offNetworkUrgent && Math.floor(time * 4) % 2 === 0;
        const color = urgentBlink ? "#ff5642" : "#ffc640";
        ctx.font = font(20);
        const w = ctx.measureText(text).width;
        const cx = HEAT_X + HEAT_W * 0.5,
            cy = -24;
        const x0 = cx - w / 2 - 9,
            y0 = cy - 15,
            x1 = cx + w / 2 + 9,
            y1 = cy + 15;
        chamfer(ctx, x0, y0, x1, y1, 5);
        ctx.fillStyle = "rgba(14,14,16,0.88)";
        ctx.fill();
        ctx.strokeStyle = color + "cc";
        ctx.lineWidth = 1.3;
        ctx.stroke();
        ctx.fillStyle = color;
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(text, cx, cy + 1);
    }

    // LiquidGauges.Draw: the shield crystal and the chest, full up to their level and dark above it, with a softly
    // waving bright surface. A low shield pulses red and flashes white when hit.
    drawLiquids(ctx, p, time) {
        const pulse = Math.floor(time * 4) % 2 === 0;
        const [sTop, sBottom] = extent(SHIELD_OUTLINE);
        const sLevel = sBottom - (sBottom - sTop) * this.shown.shield;
        if (p.hitFresh) fillBand(ctx, SHIELD_OUTLINE, sLevel, sBottom, abgr(0x90ffffff));
        else if (p.shieldLow && p.shield > 0 && pulse)
            fillBand(ctx, SHIELD_OUTLINE, sLevel, sBottom, abgr(0x703030ff));
        fillBand(ctx, SHIELD_OUTLINE, sTop, sLevel, abgr(0xc8080808));
        surface(ctx, SHIELD_OUTLINE, sLevel, p.shield > 0 ? 0xffffe0a0 : 0, time);

        const [cTop, cBottom] = extent(CHEST_OUTLINE);
        const cLevel = cBottom - (cBottom - cTop) * this.shown.chest;
        fillBand(ctx, CHEST_OUTLINE, cTop, cLevel, abgr(0xd8080808));
        surface(ctx, CHEST_OUTLINE, cLevel, p.holdFill > 0 ? 0xff80d8ff : 0, time);
    }

    // BuildPanel.Draw: a slot per machine, filling toward its cost from the stock that would pay (BuildSlotView).
    drawBuild(ctx, builds, p, time) {
        ctx.fillStyle = abgr(0xa8000000);
        for (const x of SLOT_X) {
            roundRect(ctx, BUILD_X + x, SLOT_Y[1], BUILD_X + x + SLOT_W, SLOT_Y[1] + SLOT_H, 6);
            ctx.fill();
        }
        for (const b of builds) {
            const x0 = BUILD_X + SLOT_X[b.slot],
                y0 = SLOT_Y[0],
                x1 = x0 + SLOT_W,
                y1 = y0 + SLOT_H;
            if (b.state === "Blocked") {
                ctx.fillStyle = abgr(0xa8000000);
                roundRect(ctx, x0, y0, x1, y1, 6);
                ctx.fill();
            } else if (b.state === "Filling") {
                ctx.fillStyle = abgr(0x80000000);
                roundRect(ctx, x0, y0, x1, y1 - SLOT_H * b.progress, 6);
                ctx.fill();
            }
            ctx.strokeStyle =
                b.state === "Carrying"
                    ? abgr(Math.floor(time * 3) % 2 === 0 ? 0xff40ffff : 0xff20a0a0)
                    : BUILD_STATE_BORDER[b.state];
            ctx.lineWidth = 2.5;
            roundRect(ctx, x0, y0, x1, y1, 6);
            ctx.stroke();
            outlined(ctx, b.label, x0 + 4, y1 - 18, 15, b.state === "Blocked" ? abgr(0xff909090) : "#fff");
            if (b.caption) outlined(ctx, b.caption, x0 + 4, y0 + 3, 13, CAPTION_COLOR[b.captionKind]);
        }
    }
}

function size(canvas, width, height, density) {
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
    canvas.width = Math.ceil(width * density);
    canvas.height = Math.ceil(height * density);
}

// HudStrip.DrawStripText: centred on an art point, sized in art pixels, with a soft glow, a dark outline and a
// second pass a fraction of a pixel over, to match the art's chunky lettering.
function stripText(ctx, text, cx, cy, size, color, glow) {
    ctx.font = font(size);
    ctx.textAlign = "center";
    ctx.textBaseline = "alphabetic";
    const ink = ctx.measureText("0");
    const y = cy + (ink.actualBoundingBoxAscent - ink.actualBoundingBoxDescent) / 2;
    if (glow) {
        ctx.save();
        ctx.shadowColor = glow.replace(/[\d.]+\)$/, "0.9)");
        ctx.shadowBlur = 6 * ctx.getTransform().a;
        ctx.fillStyle = glow;
        ctx.fillText(text, cx, y);
        ctx.restore();
    }
    ctx.lineJoin = "round";
    ctx.strokeStyle = abgr(0xe0000000);
    ctx.lineWidth = 2.4;
    ctx.strokeText(text, cx, y);
    ctx.strokeText(text, cx + 0.6, y);
    ctx.fillStyle = color;
    ctx.fillText(text, cx, y);
    ctx.fillText(text, cx + 0.6, y);
}

// HudDraw.DrawOutlinedText: text from its top-left corner, with a one-pixel dark outline.
function outlined(ctx, text, x, y, size, color) {
    ctx.font = font(size);
    ctx.textAlign = "left";
    ctx.textBaseline = "top";
    ctx.fillStyle = abgr(0xe0000000);
    for (const [dx, dy] of [[1, 1], [-1, 1], [1, -1], [-1, -1]]) ctx.fillText(text, x + dx, y + dy);
    ctx.fillStyle = color;
    ctx.fillText(text, x, y);
}

function extent(poly) {
    let top = Infinity,
        bottom = -Infinity;
    for (const [, y] of poly) {
        top = Math.min(top, y);
        bottom = Math.max(bottom, y);
    }
    return [top, bottom];
}

// HudDraw.FillPolygonBand: the polygon, between two heights.
function fillBand(ctx, poly, y0, y1, color) {
    if (y1 <= y0) return;
    ctx.save();
    ctx.beginPath();
    ctx.rect(-1e4, y0, 2e4, y1 - y0);
    ctx.clip();
    polygon(ctx, poly);
    ctx.fillStyle = color;
    ctx.fill();
    ctx.restore();
}

// HudDraw.PolygonSpanAtY: where a level crosses the polygon's sides.
function spanAt(poly, y) {
    let x0 = Infinity,
        x1 = -Infinity;
    for (let i = 0; i < poly.length; i++) {
        const [ax, ay] = poly[i],
            [bx, by] = poly[(i + 1) % poly.length];
        if (y < Math.min(ay, by) || y > Math.max(ay, by) || ay === by) continue;
        const x = ax + ((y - ay) / (by - ay)) * (bx - ax);
        x0 = Math.min(x0, x);
        x1 = Math.max(x1, x);
    }
    return x0 <= x1 ? [x0, x1] : [0, 0];
}

// LiquidGauges.DrawSurface: a gently waving bright line where the liquid meets the empty part.
function surface(ctx, poly, y, color, time) {
    const [x0, x1] = spanAt(poly, y);
    const half = (x1 - x0) / 2;
    if (!color || half < 3) return;
    ctx.beginPath();
    for (let i = 0; i <= 12; i++) {
        const u = i / 12;
        const px = x0 + 2 * half * u,
            py = y + Math.sin(u * 9 + time * 5) * 1.2;
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
    }
    ctx.strokeStyle = abgr(((0x50 << 24) | (color & 0xffffff)) >>> 0);
    ctx.lineWidth = 4;
    ctx.stroke();
    ctx.strokeStyle = abgr(color);
    ctx.lineWidth = 1.5;
    ctx.stroke();
}

function polygon(ctx, poly) {
    ctx.beginPath();
    poly.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
}

function circle(ctx, x, y, r) {
    ctx.beginPath();
    ctx.arc(x, y, Math.max(0, r), 0, Math.PI * 2);
}

function triangle(ctx, ax, ay, bx, by, cx, cy) {
    ctx.beginPath();
    ctx.moveTo(ax, ay);
    ctx.lineTo(bx, by);
    ctx.lineTo(cx, cy);
    ctx.closePath();
    ctx.fill();
}

function line(ctx, x, y, dx, dy, from, to, color, width) {
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.beginPath();
    ctx.moveTo(x + dx * from, y + dy * from);
    ctx.lineTo(x + dx * to, y + dy * to);
    ctx.stroke();
}

function roundRect(ctx, x0, y0, x1, y1, r) {
    ctx.beginPath();
    ctx.roundRect(x0, y0, Math.max(0, x1 - x0), Math.max(0, y1 - y0), r);
}

// HudTheme.ChamferedRect
function chamfer(ctx, x0, y0, x1, y1, c) {
    polygon(ctx, [
        [x0 + c, y0], [x1 - c, y0], [x1, y0 + c], [x1, y1 - c],
        [x1 - c, y1], [x0 + c, y1], [x0, y1 - c], [x0, y0 + c],
    ]);
}

// Two #rrggbb colours mixed, `t` of the way to the second.
function mix(a, b, t) {
    const n = (s, i) => Number.parseInt(s.slice(1 + 2 * i, 3 + 2 * i), 16);
    const c = [0, 1, 2].map((i) => Math.round(n(a, i) + (n(b, i) - n(a, i)) * t));
    return `#${c.map((v) => v.toString(16).padStart(2, "0")).join("")}`;
}
