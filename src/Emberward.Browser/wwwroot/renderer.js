import { WebGlRenderer } from "./webgl.js";
import { ResolutionGovernor } from "./pace.js";

export class GameRenderer {
    constructor(canvas) {
        this.canvas = canvas;
        this.ctx = canvas.getContext("2d");
        this.worldCanvas = document.createElement("canvas");
        this.worldCanvas.id = "world";
        this.worldCanvas.style.pointerEvents = "none";
        this.worldCanvas.setAttribute("aria-hidden", "true");
        canvas.before(this.worldCanvas);
        this.zoom = 6;
        this.frame = null;
        this.target = null;
        // The charge labels over machines (G), drawn while the frame carries them (a match with the energy network).
        this.showPower = true;
        this.governor = new ResolutionGovernor();
        this.resize();
        window.addEventListener("resize", () => this.resize());
    }

    async initialize(progress) {
        this.gpu ||= new WebGlRenderer(this.worldCanvas);
        await this.gpu.initialize(progress);
        this.gpu.resetWorld();
    }

    resize() {
        const r = this.canvas.getBoundingClientRect();
        this.width = r.width;
        this.height = r.height;
        this.dpr = Math.min(
            devicePixelRatio || 1,
            2,
            3840 / r.width,
            2160 / r.height,
        );
        this.canvas.width = Math.round(r.width * this.dpr);
        this.canvas.height = Math.round(r.height * this.dpr);
        this.overlayCleared = true;
    }

    // The cell the tutorial points at, or null (guidance.js). A change is drawn at once, not with the next frame from
    // the worker.
    get pointerTarget() {
        return this.target;
    }

    set pointerTarget(target) {
        const t = this.target;
        if (t?.x === target?.x && t?.y === target?.y) return;
        this.target = target ? { x: target.x, y: target.y } : null;
        this.overlayCleared = true;
    }

    // Device pixels per CSS pixel of the world canvas; the HUD canvas keeps this.dpr.
    get worldDpr() {
        return this.dpr * this.governor.scale;
    }

    viewSize() {
        const width = Math.max(1, Math.round(this.width * this.worldDpr)),
            height = Math.max(1, Math.round(this.height * this.worldDpr));
        const scale = Math.max(
            1,
            Math.round(this.zoom * this.worldDpr),
            Math.ceil(width / 620),
            Math.ceil(height / 380),
        );
        const powerLabelCellSize = this.showPower ? (scale * this.width) / width : 0;
        return { width, height, scale, powerLabelCellSize };
    }

    // CSS pixels per terrain cell in the frame on screen.
    get cellSize() {
        return (this.gpu.scale * this.width) / this.gpu.width;
    }

    accept(state, packet) {
        if (state) this.frame = state;
        this.gpu.accept(packet);
    }

    // `aim` is the pointer from the centre of the view in CSS pixels (input.js), or null.
    draw(now, aim) {
        const c = this.ctx;
        if (!this.frame) {
            this.worldCanvas.hidden = true;
            c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
            // The welcome screen has its own opaque illustration over the canvas; only the lobby shows this one.
            this.welcome ??= document.getElementById("welcome");
            if (this.welcome?.hidden !== false) this.drawLanding(c, now);
            // The landing illustration shares the overlay canvas: the first game frame clears it.
            this.overlayCleared = true;
            return;
        }
        this.worldCanvas.hidden = false;
        // Nothing new from the worker since the last draw: what is on screen is still right, unless the overlay
        // changed or animates (the tutorial's pointer pulses).
        // While a hit shakes the view the mouse cursor gives way to a crosshair that shakes with it (see below).
        const shaken = Boolean(aim?.mouse && this.gpu.hit > 0);
        if (shaken !== this.shaken) {
            this.shaken = shaken;
            document.body.classList.toggle("aim-shaken", shaken);
        }
        if (!this.gpu.draw() && !this.overlayCleared && !this.target && !shaken)
            return;
        c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
        // The overlay is empty most of the time: clear it only when something was drawn on it (or the canvas was
        // resized), rather than filling the whole display every frame.
        if (this.overlayCleared || this.overlayDrawn)
            c.clearRect(0, 0, this.width, this.height);
        this.overlayCleared = false;
        let drawn = false;
        if (this.frame.goal)
            drawn =
                this.bearing(c, this.frame.goal.position, "CORE", "#e9a367") ||
                drawn;
        if (!this.frame.player.docked)
            drawn =
                this.bearing(c, this.frame.player.home, "HOME", "#83d9cf") ||
                drawn;
        if (this.target) drawn = this.pointer(c, this.target, now) || drawn;
        drawn = this.machineUpgrade(c, this.frame.machineUpgrade) || drawn;
        if (shaken) drawn = this.crosshair(c, aim) || drawn;
        this.overlayDrawn = drawn;
    }

    // The crosshair where the effect pass shows the point the mouse aims at, so it stays on it through a hit's zoom
    // and shake, drawn as on the desktop. Kept inside the view (ScreenEffectLevels.ShownInView): near an edge the
    // zoom and shake would push it out, and with the cursor hidden the player would have nothing to aim with.
    crosshair(c, aim) {
        const p = this.gpu.shownFromCentre(aim.x, aim.y, this.height);
        const x = Math.min(Math.max(this.width / 2 + p.x, 0), this.width - 1),
            y = Math.min(Math.max(this.height / 2 + p.y, 0), this.height - 1),
            arm = 10,
            gap = 3;
        c.save();
        c.strokeStyle = "#fff";
        c.lineWidth = 2;
        c.beginPath();
        c.moveTo(x - arm, y);
        c.lineTo(x - gap, y);
        c.moveTo(x + gap, y);
        c.lineTo(x + arm, y);
        c.moveTo(x, y - arm);
        c.lineTo(x, y - gap);
        c.moveTo(x, y + gap);
        c.lineTo(x, y + arm);
        c.stroke();
        c.restore();
        return true;
    }

    // The machine next to the tank (MachineUpgradeView, shared with the desktop): a ring in its highlight colour, its
    // level pips under it (its charge label is above) and, while its panel is closed, the key that opens it. The
    // panel (hud.js) is kept beside it here, since the machine moves on screen with the camera between HUD updates.
    // Returns whether it drew.
    machineUpgrade(c, m) {
        this.machinePanel ??= document.getElementById("machine-upgrade");
        const scale = this.cellSize;
        const { dx, dy } = m ? this.fromCentre(m.position) : { dx: 0, dy: 0 };
        const x = this.width / 2 + dx,
            y = this.height / 2 + dy,
            r = m ? m.ringCells * scale : 0;
        const onScreen =
            m && x > -r && y > -r && x < this.width + r && y < this.height + r;
        if (this.machinePanel && m?.open && onScreen) {
            const left = x + r + 14 + this.machinePanel.offsetWidth > this.width;
            const panelX = left ? x - r - 14 - this.machinePanel.offsetWidth : x + r + 14;
            // Level with the machine, but kept inside the view (MachineUpgradeView.PanelCentreY, as on the desktop):
            // on a short screen (a phone held sideways) a machine below the tank would otherwise push the panel's
            // lower rows off the bottom.
            const half = this.machinePanel.offsetHeight / 2;
            const panelY = Math.max(half + 4, Math.min(this.height - half - 4, y));
            const panelLeft = `${Math.round(Math.max(4, panelX))}px`,
                top = `${Math.round(panelY)}px`;
            if (this.machinePanel.style.left !== panelLeft) this.machinePanel.style.left = panelLeft;
            if (this.machinePanel.style.top !== top) this.machinePanel.style.top = top;
        }
        if (!onScreen) return false;
        c.save();
        if (m.ring) {
            c.strokeStyle = m.ring;
            c.globalAlpha = m.bright ? 1 : 0.7;
            c.lineWidth = m.open ? 3 : 2;
            c.beginPath();
            c.arc(x, y, r, 0, Math.PI * 2);
            c.stroke();
            c.globalAlpha = 1;
        }
        const pip = 8,
            step = 11,
            py = y + m.pipDropCells * scale;
        const px = x - (m.maxLevel * step - (step - pip)) / 2;
        for (let i = 0; i < m.maxLevel; i++) {
            c.fillStyle = i < m.level ? "#ffb240" : "#08080ae6";
            c.fillRect(px + i * step, py - pip / 2, pip, pip * 0.8);
            c.strokeStyle = "#3a2816";
            c.lineWidth = 1;
            c.strokeRect(px + i * step, py - pip / 2, pip, pip * 0.8);
        }
        if (!m.open) {
            c.font = "bold 11px monospace";
            c.fillStyle = m.bright ? "#ffb240" : "#9e968a";
            c.textAlign = "left";
            c.fillText("F", x + r + 4, y + 4);
        }
        c.restore();
        return true;
    }

    // Whether `p`, a point from the centre of the view in CSS pixels (input.js), lies on a machine that takes the tap:
    // a lit one, or any while its panel is open (MachineUpgradeView.CatchesTaps). A tap near a maxed machine fires.
    machineAt(p) {
        const m = this.frame?.machineUpgrade;
        if (!m?.tappable || !p) return false;
        const { dx, dy } = this.fromCentre(m.position);
        return Math.hypot(p.x - dx, p.y - dy) <= m.ringCells * this.cellSize + 8;
    }

    // Where a cell is on screen, from the centre of the view, in CSS pixels.
    fromCentre(target) {
        const scale = this.cellSize;
        return {
            dx: (target.x + 0.5 - this.gpu.cameraX) * scale - this.width / 2,
            dy: (target.y + 0.5 - this.gpu.cameraY) * scale - this.height / 2,
        };
    }

    // The tutorial's target, as on the desktop: a pulsing ring with a bobbing arrow when it is on screen, otherwise a
    // bearing at the edge of the view.
    pointer(c, target, now) {
        const { dx, dy } = this.fromCentre(target);
        const margin = 40;
        if (
            Math.abs(dx) > this.width / 2 - margin ||
            Math.abs(dy) > this.height / 2 - margin
        ) {
            return this.bearing(c, target, "NEXT", "#ecb867", margin);
        }
        const t = now / 1000,
            pulse = 0.5 + 0.5 * Math.sin(t * 5);
        const x = this.width / 2 + dx,
            y = this.height / 2 + dy,
            r = 18 + 4 * pulse;
        c.save();
        c.strokeStyle = `rgba(236, 184, 103, ${0.5 + 0.5 * pulse})`;
        c.lineWidth = 2;
        c.beginPath();
        c.arc(x, y, r, 0, Math.PI * 2);
        c.stroke();
        c.strokeStyle = `rgba(236, 184, 103, ${0.2 + 0.2 * pulse})`;
        c.lineWidth = 1;
        c.beginPath();
        c.arc(x, y, r + 4, 0, Math.PI * 2);
        c.stroke();
        const tip = y - r - 4 + 6 * Math.sin(t * 4);
        c.fillStyle = "#ecb867";
        c.beginPath();
        c.moveTo(x, tip);
        c.lineTo(x - 8, tip - 13);
        c.lineTo(x + 8, tip - 13);
        c.closePath();
        c.fill();
        c.restore();
        return true;
    }

    // Draws an arrow at the edge of the view toward a cell that is off screen, or within `inset` pixels of an edge;
    // returns whether it drew.
    bearing(c, target, label, color, inset = 0) {
        const scale = this.cellSize;
        const { dx, dy } = this.fromCentre(target);
        const w = this.width / 2 - Math.max(28, inset),
            h = this.height / 2 - Math.max(30, inset);
        if (Math.abs(dx) < w && Math.abs(dy) < h) return false;
        const ratio = Math.min(
            w / Math.max(Math.abs(dx), 1),
            h / Math.max(Math.abs(dy), 1),
        );
        const x = this.width / 2 + dx * ratio,
            y = this.height / 2 + dy * ratio;
        c.save();
        c.translate(x, y);
        c.rotate(Math.atan2(dy, dx));
        c.fillStyle = color;
        c.beginPath();
        c.moveTo(7, 0);
        c.lineTo(-4, -4);
        c.lineTo(-4, 4);
        c.closePath();
        c.fill();
        c.restore();
        c.fillStyle = color;
        c.font = "9px monospace";
        c.textAlign = dx < 0 ? "left" : "right";
        c.fillText(
            `${label} ${Math.round(Math.hypot(dx, dy) / scale)}`,
            x + (dx < 0 ? 10 : -10),
            y - 10,
        );
        return true;
    }

    drawLanding(c, now) {
        c.fillStyle = "#0b1419";
        c.fillRect(0, 0, this.width, this.height);
        const cx = this.width * 0.79,
            cy = this.height * 0.48;
        const glow = c.createRadialGradient(
            cx,
            cy,
            5,
            cx,
            cy,
            this.height * 0.7,
        );
        glow.addColorStop(0, "#72422355");
        glow.addColorStop(1, "#0b141900");
        c.fillStyle = glow;
        c.fillRect(0, 0, this.width, this.height);
        c.strokeStyle = "#65847815";
        c.lineWidth = 1;
        for (let r = 40; r < this.height * 0.8; r += 22) {
            c.beginPath();
            for (let i = 0; i <= 100; i++) {
                const a = (i / 100) * Math.PI * 2;
                const radius =
                    r + Math.sin(a * 5 + r / 100) * 14 + Math.cos(a * 9) * 7;
                const x = cx + Math.cos(a) * radius * 1.05,
                    y = cy + Math.sin(a) * radius * 0.8;
                if (i === 0) c.moveTo(x, y);
                else c.lineTo(x, y);
            }
            c.closePath();
            c.stroke();
        }
        c.fillStyle = "#eeb065";
        c.beginPath();
        c.arc(cx, cy, 3, 0, Math.PI * 2);
        c.fill();
        c.strokeStyle = "#eeb06555";
        c.beginPath();
        c.arc(cx, cy, 13 + Math.sin(now / 1000) * 3, 0, Math.PI * 2);
        c.stroke();
        c.font = "10px monospace";
        c.fillStyle = "#ad916c";
        c.fillText("CORE / UNKNOWN DEPTH", cx + 25, cy + 4);
        const fade = c.createLinearGradient(0, 0, this.width, 0);
        fade.addColorStop(0, "#0b1419");
        fade.addColorStop(0.5, "#0b1419bb");
        fade.addColorStop(1, "#0b141900");
        c.fillStyle = fade;
        c.fillRect(0, 0, this.width, this.height);
    }
}
