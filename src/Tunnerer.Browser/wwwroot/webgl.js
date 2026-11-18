// Index of PostParamsBlock.ScreenFxActive among the frame's uniform floats (PostParamsLayoutGuardTests checks it).
const SCREEN_FX_ACTIVE = 71;

// A failure of the graphics hardware or driver, as opposed to one of loading the game.
export class GraphicsError extends Error {
    name = "GraphicsError";
}

// Draws the desktop shaders, translated at publish time from their original HLSL.
export class WebGlRenderer {
    constructor(canvas) {
        this.canvas = canvas;
        this.gl = canvas.getContext("webgl2", {
            alpha: false,
            antialias: false,
            depth: false,
            stencil: false,
            // The procedural terrain is pixel-heavy: ask for the discrete GPU where a machine has two.
            powerPreference: "high-performance",
            // A software renderer stalls on these shaders until the browser drops the context. Say so at once.
            failIfMajorPerformanceCaveat: true,
        });
        if (!this.gl) {
            const software = document
                .createElement("canvas")
                .getContext("webgl2");
            throw new GraphicsError(
                software
                    ? "This browser is drawing without graphics acceleration, which is too slow for the game. Turn on hardware acceleration in its settings and reload the page"
                    : "WebGL 2 is unavailable. Use a current browser with hardware acceleration turned on",
            );
        }
        this.mapTextures = [];
        this.mapWidth = this.mapHeight = 0;
        this.targetWidth = this.targetHeight = 0;
        this.ready = false;
        this.samplerBindings = new Map();
        canvas.addEventListener("webglcontextlost", (e) => {
            e.preventDefault();
            this.error = new GraphicsError(
                "The graphics context was lost. Reload the page to restore the game",
            );
        });
    }

    async initialize() {
        if (this.ready) return;
        const gl = this.gl;
        const names = [
            "FullscreenVs",
            "FullscreenTargetVs",
            "TerrainPs",
            "PostPs",
            "EntityVs",
            "EntityPs",
            "ScreenFxPs",
        ];
        const source = Object.fromEntries(
            await Promise.all(
                names.map(async (name) => {
                    const response = await fetch(
                        new URL(`./shaders/${name}.glsl`, import.meta.url),
                    );
                    if (!response.ok)
                        throw new Error(
                            `Could not load ${name} shader (${response.status}).`,
                        );
                    return [name, await response.text()];
                }),
            ),
        );
        // All compile at once where the driver can; each is awaited, so the page is never blocked on one.
        [this.terrainProgram, this.postProgram, this.entityProgram, this.screenFxProgram] =
            await Promise.all([
                this.program("Terrain", source.FullscreenTargetVs, source.TerrainPs),
                this.program("Post", source.FullscreenVs, source.PostPs),
                this.program("Entity", source.EntityVs, source.EntityPs),
                this.program("Screen effects", source.FullscreenVs, source.ScreenFxPs),
            ]);
        this.uniforms = gl.createBuffer();
        gl.bindBufferBase(gl.UNIFORM_BUFFER, 0, this.uniforms);
        this.entityBuffer = gl.createBuffer();
        this.entityVao = gl.createVertexArray();
        this.emptyVao = gl.createVertexArray();
        gl.bindVertexArray(this.entityVao);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.entityBuffer);
        let offset = 0;
        for (const [index, count] of [2, 2, 4, 4].entries()) {
            gl.enableVertexAttribArray(index);
            gl.vertexAttribPointer(index, count, gl.FLOAT, false, 48, offset);
            offset += count * 4;
        }
        gl.bindVertexArray(null);
        this.framebuffer = gl.createFramebuffer();
        this.sceneTexture = this.texture();
        // The finished game image, drawn here instead of to the canvas while a whole-screen effect shows.
        this.screenFramebuffer = gl.createFramebuffer();
        this.screenTexture = this.texture();
        gl.disable(gl.DEPTH_TEST);
        gl.disable(gl.CULL_FACE);
        this.ready = true;
    }

    // Compiles and links one program. With KHR_parallel_shader_compile the driver does it off the main thread and
    // this waits a frame at a time for it; without, the first status query below waits for the driver.
    async program(name, vertex, fragment) {
        const gl = this.gl,
            parallel = gl.getExtension("KHR_parallel_shader_compile"),
            started = performance.now(),
            program = gl.createProgram();
        const shaders = [
            [gl.VERTEX_SHADER, vertex],
            [gl.FRAGMENT_SHADER, fragment],
        ].map(([type, source]) => {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            gl.attachShader(program, shader);
            return shader;
        });
        // Linking before asking for the compile status lets the driver keep working on both in the background.
        gl.linkProgram(program);
        if (parallel)
            while (
                !gl.getProgramParameter(program, parallel.COMPLETION_STATUS_KHR)
            ) {
                this.checkContext();
                await new Promise((resolve) => setTimeout(resolve, 16));
            }
        this.checkContext();
        for (const shader of shaders) {
            if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
                const error = gl.getShaderInfoLog(shader);
                throw new GraphicsError(
                    `Shader compilation failed (${name}): ${error || "the driver gave no reason"}`,
                );
            }
            gl.detachShader(program, shader);
            gl.deleteShader(shader);
        }
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            this.checkContext();
            throw new GraphicsError(
                `Shader linking failed (${name}): ${gl.getProgramInfoLog(program) || "the driver gave no reason"}`,
            );
        }
        console.info(
            `[graphics] ${name} shaders ready in ${Math.round(performance.now() - started)} ms` +
                (parallel ? " (compiled in parallel)" : ""),
        );
        const block = gl.getUniformBlockIndex(program, "PostParamsBlock");
        if (block !== gl.INVALID_INDEX)
            gl.uniformBlockBinding(program, block, 0);
        return program;
    }

    // A lost context answers every query with a default, so a status poll would wait forever: stop instead.
    checkContext() {
        if (this.error) throw this.error;
        if (this.gl.isContextLost())
            throw new GraphicsError(
                "The graphics driver stopped while preparing the shaders. Reload the page to try again",
            );
    }

    // Every texture has level 0 only and filters without mipmaps: the web shaders sample level 0 explicitly
    // (textureLod, tools/Tunnerer.ShaderCompiler/ExplicitLodRewrite.cs), which is the same only while that holds.
    texture() {
        const gl = this.gl,
            texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        return texture;
    }

    resetWorld() {
        for (const texture of this.mapTextures) this.gl.deleteTexture(texture);
        this.mapTextures = [];
        this.mapWidth = this.mapHeight = 0;
        this.hasFrame = false;
    }

    // Takes one frame from the worker: a header of int32 values, then the parts it points at (BrowserScene.cs).
    // Every packet must be taken, in order: each carries only the texels that changed since the one before.
    accept(packet) {
        const gl = this.gl;
        const header = new Int32Array(packet.buffer, packet.byteOffset, 24);
        const uniformBytes = packet.subarray(header[0], header[0] + header[1]);
        const uniforms = new Float32Array(
            packet.buffer,
            packet.byteOffset + header[0],
            header[1] / 4,
        );
        const vertices = packet.subarray(header[2], header[2] + header[3]);
        const width = uniforms[4],
            height = uniforms[5];
        if (this.mapWidth !== width || this.mapHeight !== height) {
            this.resetWorld();
            this.mapWidth = width;
            this.mapHeight = height;
            for (let i = 0; i < 4; i++) {
                const texture = this.texture();
                gl.texImage2D(
                    gl.TEXTURE_2D,
                    0,
                    gl.RGBA8,
                    width,
                    height,
                    0,
                    gl.RGBA,
                    gl.UNSIGNED_BYTE,
                    null,
                );
                this.mapTextures.push(texture);
            }
            this.postTextures = [
                this.sceneTexture,
                ...this.mapTextures.slice(1),
            ];
        }
        this.uploadedBytes = 0;
        for (let i = 0; i < 4; i++) {
            const at = 4 + 5 * i,
                offset = header[at],
                w = header[at + 3],
                h = header[at + 4];
            if (w <= 0 || h <= 0) continue;
            gl.bindTexture(gl.TEXTURE_2D, this.mapTextures[i]);
            gl.texSubImage2D(
                gl.TEXTURE_2D,
                0,
                header[at + 1],
                header[at + 2],
                w,
                h,
                gl.RGBA,
                gl.UNSIGNED_BYTE,
                packet.subarray(offset, offset + w * h * 4),
            );
            this.uploadedBytes += w * h * 4;
        }
        this.width = uniforms[8];
        this.height = uniforms[9];
        this.scale = uniforms[2];
        this.cameraX = uniforms[6] / this.scale;
        this.cameraY = uniforms[7] / this.scale;
        gl.bindBuffer(gl.UNIFORM_BUFFER, this.uniforms);
        gl.bufferData(gl.UNIFORM_BUFFER, uniformBytes, gl.DYNAMIC_DRAW);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.entityBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.DYNAMIC_DRAW);
        this.vertexCount = vertices.byteLength / 48;
        this.screenEffects = uniforms[SCREEN_FX_ACTIVE] > 0.5;
        this.hasFrame = true;
        this.fresh = true;
    }

    bindTextures(program, textures, sceneName) {
        const gl = this.gl;
        gl.useProgram(program);
        if (!this.samplerBindings.has(program)) {
            const count = gl.getProgramParameter(program, gl.ACTIVE_UNIFORMS);
            for (let i = 0; i < count; i++) {
                const uniform = gl.getActiveUniform(program, i);
                if (uniform.type !== gl.SAMPLER_2D) continue;
                const unit = uniform.name.startsWith(sceneName)
                    ? 0
                    : uniform.name.startsWith("auxTex")
                      ? 1
                      : uniform.name.startsWith("coverageTex")
                        ? 2
                        : 3;
                gl.uniform1i(
                    gl.getUniformLocation(program, uniform.name),
                    unit,
                );
            }
            this.samplerBindings.set(program, true);
        }
        for (let unit = 0; unit < textures.length; unit++) {
            gl.activeTexture(gl.TEXTURE0 + unit);
            gl.bindTexture(gl.TEXTURE_2D, textures[unit]);
        }
    }

    // Draws the last accepted frame once; false when there is nothing new to draw.
    draw() {
        if (this.error) throw this.error;
        if (!this.hasFrame || !this.fresh) return false;
        this.fresh = false;
        const gl = this.gl;
        if (
            this.targetWidth !== this.width ||
            this.targetHeight !== this.height
        ) {
            this.targetWidth = this.width;
            this.targetHeight = this.height;
            this.canvas.width = this.width;
            this.canvas.height = this.height;
            this.renderTarget(this.framebuffer, this.sceneTexture, "terrain");
            this.renderTarget(this.screenFramebuffer, this.screenTexture, "screen effects");
        }
        gl.viewport(0, 0, this.width, this.height);
        gl.disable(gl.BLEND);
        gl.bindVertexArray(this.emptyVao);
        gl.bindFramebuffer(gl.FRAMEBUFFER, this.framebuffer);
        this.bindTextures(this.terrainProgram, this.mapTextures, "sourceTex");
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        // Whole-screen effects cost a pass only while one shows.
        gl.bindFramebuffer(
            gl.FRAMEBUFFER,
            this.screenEffects ? this.screenFramebuffer : null,
        );
        this.bindTextures(
            this.postProgram,
            this.postTextures,
            "sceneTex",
        );
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.useProgram(this.entityProgram);
        gl.bindVertexArray(this.entityVao);
        gl.drawArrays(gl.TRIANGLES, 0, this.vertexCount);
        gl.disable(gl.BLEND);
        if (this.screenEffects) {
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.bindVertexArray(this.emptyVao);
            this.bindTextures(
                this.screenFxProgram,
                [this.screenTexture],
                "sceneTex",
            );
            gl.drawArrays(gl.TRIANGLES, 0, 3);
        }
        gl.bindVertexArray(null);
        return true;
    }

    // Sizes a texture to the view and makes it the colour target of the framebuffer.
    renderTarget(framebuffer, texture, name) {
        const gl = this.gl;
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(
            gl.TEXTURE_2D,
            0,
            gl.RGBA8,
            this.width,
            this.height,
            0,
            gl.RGBA,
            gl.UNSIGNED_BYTE,
            null,
        );
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(
            gl.FRAMEBUFFER,
            gl.COLOR_ATTACHMENT0,
            gl.TEXTURE_2D,
            texture,
            0,
        );
        if (
            gl.checkFramebufferStatus(gl.FRAMEBUFFER) !==
            gl.FRAMEBUFFER_COMPLETE
        )
            throw new GraphicsError(`Could not create the ${name} render target.`);
    }
}
