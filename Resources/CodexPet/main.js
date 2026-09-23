const { ipcRenderer } = require('electron');

const canvas = document.getElementById('petCanvas');
const ctx = canvas.getContext('2d');
const subtitleEl = document.getElementById('subtitle');

// Codex v2 精灵表规范
const CELL_WIDTH = 192;
const CELL_HEIGHT = 208;

// 各行动画的有效帧数
const ROW_FRAMES = [6, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8];

// 标准动作行的每帧持续时间（ms）
const ROW_DURATIONS = [
    [280, 110, 110, 140, 140, 320],
    [120, 120, 120, 120, 120, 120, 120, 220],
    [120, 120, 120, 120, 120, 120, 120, 220],
    [140, 140, 140, 280],
    [140, 140, 140, 140, 280],
    [140, 140, 140, 140, 140, 140, 140, 240],
    [150, 150, 150, 150, 150, 260],
    [120, 120, 120, 120, 120, 220],
    [150, 150, 150, 150, 150, 280],
    [150, 150, 150, 150, 150, 150, 150, 150],
    [150, 150, 150, 150, 150, 150, 150, 150]
];

// 行号常量
const ROW_IDLE = 0;
const ROW_RUNNING_RIGHT = 1;
const ROW_RUNNING_LEFT = 2;

let spritesheet = null;
let currentRow = ROW_IDLE;
let currentFrame = 0;
let isLooping = true;
let lastFrameTime = 0;
let lookFrame = -1;
let isUsing = false;
let glowEnabled = false;
let outlineEnabled = false;
let outlineColor = "#000000";
let mouseLookEnabled = false;
let mouseLookFrame = -1;
let animationSpeed = 1.0;
let randomMotionTimer = null;
let randomMotionCooldownUntil = 0;
// 精灵表版本：v1 = 8x9（rows 0-8），v2 = 8x11（额外 rows 9-10 注视）
let atlasVersion = 2;
let atlasRowCount = 11;
// 窗口顶部气泡预留高度（主进程 setPetArea 下发）
let petAreaHeight = 0;

const petChannel = new URLSearchParams(location.search).get('petChannel') || 'codex-pet';

function setAtlasInfo(version, rows) {
    atlasVersion = Number(version) === 2 ? 2 : 1;
    atlasRowCount = atlasVersion === 2 ? 11 : 9;
    if (Number(rows) === 9 || Number(rows) === 11) {
        atlasRowCount = Number(rows);
    }
}

function resizeCanvas() {
    canvas.width = window.innerWidth;
    canvas.height = Math.max(1, window.innerHeight - petAreaHeight);
    canvas.style.top = petAreaHeight + 'px';
}

function draw() {
    if (!spritesheet) {
        requestAnimationFrame(draw);
        return;
    }

    ctx.clearRect(0, 0, canvas.width, canvas.height);

    let row = currentRow;
    let frame = currentFrame;

    let activeLookFrame = lookFrame;
    if (
        activeLookFrame < 0 &&
        mouseLookEnabled &&
        mouseLookFrame >= 0 &&
        currentRow === ROW_IDLE
    ) {
        activeLookFrame = mouseLookFrame;
    }

    if (activeLookFrame >= 0 && atlasRowCount >= 11) {
        if (activeLookFrame < 8) {
            row = 9;
            frame = activeLookFrame;
        } else {
            row = 10;
            frame = activeLookFrame - 8;
        }
    }

    if (row >= atlasRowCount) {
        row = ROW_IDLE;
        frame = frame % ROW_FRAMES[ROW_IDLE];
    }

    const sx = frame * CELL_WIDTH;
    const sy = row * CELL_HEIGHT;

    // 按精灵表 cell 比例 (192:208) 居中适配宠物区域，避免变形
    const fitScale = Math.min(canvas.width / CELL_WIDTH, canvas.height / CELL_HEIGHT);
    const drawWidth = CELL_WIDTH * fitScale;
    const drawHeight = CELL_HEIGHT * fitScale;
    const originX = (canvas.width - drawWidth) / 2;
    const originY = (canvas.height - drawHeight) / 2;

    ctx.drawImage(
        spritesheet,
        sx, sy, CELL_WIDTH, CELL_HEIGHT,
        originX, originY, drawWidth, drawHeight
    );

    requestAnimationFrame(draw);
}

function updateAnimation(timestamp) {
    if (!spritesheet) return;

    const durations = ROW_DURATIONS[currentRow];
    const frameCount = ROW_FRAMES[currentRow];
    const baseDuration = durations[Math.min(currentFrame, durations.length - 1)];
    const duration = baseDuration / animationSpeed;

    if (timestamp - lastFrameTime >= duration) {
        lastFrameTime = timestamp;
        currentFrame++;

        if (currentFrame >= frameCount) {
            if (isLooping) {
                currentFrame = 0;
            } else {
                currentFrame = frameCount - 1;
            }
        }
    }
}

function animate(timestamp) {
    updateAnimation(timestamp);
    requestAnimationFrame(animate);
}

function loadSpritesheet(url) {
    return new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => {
            spritesheet = img;
            resolve();
        };
        img.onerror = reject;
        img.src = url;
    });
}

let motionEndTimer = null;

function clearMotionEndTimer() {
    if (motionEndTimer !== null) {
        clearTimeout(motionEndTimer);
        motionEndTimer = null;
    }
}

function getMotionDurationMs(row) {
    const durations = ROW_DURATIONS[row] || [];
    const frameCount = ROW_FRAMES[row] || durations.length;
    let total = 0;
    for (let i = 0; i < frameCount; i++) {
        total += durations[Math.min(i, durations.length - 1)] / animationSpeed;
    }
    return total;
}

function playMotion(row, loop, durationMs = null) {
    clearMotionEndTimer();
    if (!Number.isInteger(row) || row < 0 || row >= atlasRowCount) {
        row = ROW_IDLE;
    }
    currentRow = row;
    currentFrame = 0;
    isLooping = loop;
    lastFrameTime = performance.now();
    lookFrame = -1;

    if (loop) {
        // 循环动作只在显式传时长时回 idle（用于点击/对话惊喜动作）。
        if (typeof durationMs === "number" && durationMs > 0) {
            motionEndTimer = setTimeout(() => playMotion(ROW_IDLE, true), durationMs);
        }
        return;
    }

    // 非循环动作播完必须回 idle；否则会永远停在最后一帧。
    // 有些自制/损坏精灵表最后一帧可能是空的，回 idle 也能避免“消失”。
    motionEndTimer = setTimeout(() => playMotion(ROW_IDLE, true), getMotionDurationMs(row) + 30);
}

function setLookDirection(frame) {
    if (atlasRowCount < 11 || Number(frame) < 0 || Number(frame) > 15) {
        lookFrame = -1;
        return;
    }
    lookFrame = frame;
}

function clearLookDirection() {
    lookFrame = -1;
}

function setMouseLookEnabled(enabled) {
    mouseLookEnabled = Boolean(enabled);
    if (!mouseLookEnabled) {
        mouseLookFrame = -1;
    }
}

function setMouseLookFrame(frame) {
    if (!mouseLookEnabled || atlasRowCount < 11) {
        mouseLookFrame = -1;
        return;
    }
    const value = Number(frame);
    mouseLookFrame = value >= 0 && value <= 15 ? value : -1;
}

function setUsing(value) {
    isUsing = value;
    updateCanvasFilter();
}

function updateCanvasFilter() {
    const filters = [];
    if (glowEnabled && isUsing) {
        filters.push("brightness(1.08)");
    }
    if (outlineEnabled) {
        // 8 向 drop-shadow 组成 1px 描边；WebP 透明区域不会被填充。
        for (let i = 0; i < 8; i++) {
            const x = Math.round(Math.cos(i * Math.PI / 4) * 1);
            const y = Math.round(Math.sin(i * Math.PI / 4) * 1);
            filters.push(`drop-shadow(${x}px ${y}px 0 ${outlineColor})`);
        }
    }
    canvas.style.filter = filters.length > 0 ? filters.join(" ") : "none";
}

function setVisualStyle(glow, outline, color) {
    glowEnabled = Boolean(glow);
    outlineEnabled = Boolean(outline);
    outlineColor = color || "#000000";
    updateCanvasFilter();
}

function setAnimationSpeed(speed) {
    animationSpeed = Math.max(0.25, speed);
}

function setBubbleStyle(fontSize, scale) {
    const safeFontSize = Math.min(20, Math.max(10, Number(fontSize) || 13));
    const safeScale = Math.min(2, Math.max(0.6, Number(scale) || 0.85));
    document.documentElement.style.setProperty("--bubble-font-size", safeFontSize + "px");
    document.documentElement.style.setProperty("--bubble-scale", String(safeScale));
}

// ---- 消息气泡 ----
// Alife 的 XML 流式执行器会按标点把一次 <speak> 分成多个 Content 调用。
// 这里必须把每个 showSubtitle 片段追加进同一个播放队列；如果直接替换，
// 就会出现“上一段还没播完，下一段到达后丢失剩余文本”的问题。
// hideSubtitle 也延迟到队列末尾处理，避免 Closing 消息截断还没播放的内容。
// 气泡样式固定为 8 字/行，最多 4 行；单段最多 28 个字符（含标点）。
// 分段时优先在 28 字内找最近的句号/感叹/问号；没有句末符号时找逗号等停顿符号。
const subtitleSegmentLength = 28;
const subtitleMinDurationMs = 1500;
const subtitleDurationPerCharMs = 120;
const subtitleIdleTimeoutMs = 2000;
const subtitlePendingFlushTimeoutMs = 500;

const subtitleSentenceMarks = new Set(["。", "．", ".", "！", "!", "？", "?", "…"]);
const subtitlePauseMarks = new Set(["，", ",", "、", "：", ":", "；", ";"]);

let subtitleTimeout = null;
let subtitleIdleTimer = null;
let subtitleHideQueued = false;
const subtitleQueue = [];
let subtitleBuffer = "";

function findSubtitleBreakIndex(chars) {
    if (chars.length <= subtitleSegmentLength) {
        return chars.length;
    }

    // 优先使用距离 28 字最近的句末符号。
    for (let i = subtitleSegmentLength - 1; i >= 0; i--) {
        if (subtitleSentenceMarks.has(chars[i])) {
            return i + 1;
        }
    }

    // 没有句末符号时，使用最近的逗号/顿号等停顿符号。
    for (let i = subtitleSegmentLength - 1; i >= 0; i--) {
        if (subtitlePauseMarks.has(chars[i])) {
            return i + 1;
        }
    }

    // 连续 28 字都没有符号时才硬切。
    return subtitleSegmentLength;
}

function enqueuePendingSubtitle(force = false) {
    let pushed = false;

    while (subtitleBuffer.length > 0) {
        const chars = Array.from(subtitleBuffer);

        if (chars.length > subtitleSegmentLength) {
            const take = findSubtitleBreakIndex(chars);
            subtitleQueue.push(chars.slice(0, take).join(""));
            subtitleBuffer = chars.slice(take).join("");
            pushed = true;
            continue;
        }

        const lastChar = chars[chars.length - 1];
        if (force || subtitleSentenceMarks.has(lastChar) || subtitlePauseMarks.has(lastChar)) {
            subtitleQueue.push(subtitleBuffer);
            subtitleBuffer = "";
            pushed = true;
        }
        break;
    }

    return pushed;
}

// ---- 对话中的低概率惊喜动作 ----
// Codex v2 共 9 个动作：0=idle，1/2=左右移动；这里使用剩余 6 个动作。
// 触发条件刻意保守：对话较长、已经显示到第 2 段以后、当前没有其它动作，
// 且只有 4% 概率命中；另外 45 秒内最多触发一次，避免频繁打断对话。
const subtitleMinConversationChars = 36;
const subtitleMinConversationDurationMs = 6000;
const subtitleMinConversationSegment = 2;
const subtitleMotionChance = 0.04;
const subtitleMotionCooldownMs = 45000;

const conversationMotionOptions = [
    { row: 3, durationMs: 700,  weight: 20 }, // waving
    { row: 4, durationMs: 840,  weight: 22 }, // jumping
    { row: 5, durationMs: 1400, weight: 8  }, // failed（低概率）
    { row: 6, durationMs: 2500, weight: 18 }, // waiting
    { row: 7, durationMs: 2200, weight: 12 }, // running
    { row: 8, durationMs: 2500, weight: 20 }  // review
];

let conversationStartedAt = 0;
let conversationCharCount = 0;
let conversationSegmentShown = 0;
let conversationMotionTriggered = false;
let conversationMotionCooldownUntil = 0;

function hideSubtitleNow() {
    subtitleEl.classList.remove("visible");
    subtitleEl.textContent = "";
    subtitleBuffer = "";
    conversationStartedAt = 0;
    conversationCharCount = 0;
    conversationSegmentShown = 0;
    conversationMotionTriggered = false;
}

function clearSubtitlePlayback() {
    if (subtitleTimeout !== null) {
        clearTimeout(subtitleTimeout);
        subtitleTimeout = null;
    }
    if (subtitleIdleTimer !== null) {
        clearTimeout(subtitleIdleTimer);
        subtitleIdleTimer = null;
    }
    subtitleQueue.length = 0;
    subtitleBuffer = "";
    subtitleHideQueued = false;
    conversationStartedAt = 0;
    conversationCharCount = 0;
    conversationSegmentShown = 0;
    conversationMotionTriggered = false;
}

function pickConversationMotion() {
    const totalWeight = conversationMotionOptions.reduce((sum, item) => sum + item.weight, 0);
    let roll = Math.random() * totalWeight;

    for (const item of conversationMotionOptions) {
        roll -= item.weight;
        if (roll < 0) {
            return item;
        }
    }

    return conversationMotionOptions[conversationMotionOptions.length - 1];
}

function maybeTriggerConversationMotion() {
    if (conversationMotionTriggered) return;
    if (Date.now() < conversationMotionCooldownUntil) return;
    if (currentRow !== ROW_IDLE || lookFrame >= 0) return;

    if (conversationSegmentShown < subtitleMinConversationSegment) return;
    if (conversationCharCount < subtitleMinConversationChars) return;
    if (Date.now() - conversationStartedAt < subtitleMinConversationDurationMs) return;
    if (Math.random() >= subtitleMotionChance) return;

    const item = pickConversationMotion();
    conversationMotionTriggered = true;
    conversationMotionCooldownUntil = Date.now() + subtitleMotionCooldownMs;
    playRandomMotion(item.row, item.durationMs);
}

function showNextSubtitleSegment() {
    subtitleTimeout = null;
    if (subtitleIdleTimer !== null) {
        clearTimeout(subtitleIdleTimer);
        subtitleIdleTimer = null;
    }

    if (subtitleQueue.length > 0) {
        const segment = subtitleQueue.shift();
        subtitleEl.textContent = segment;
        subtitleEl.classList.add("visible");
        conversationSegmentShown++;
        maybeTriggerConversationMotion();

        const duration = Math.max(
            segment.length * subtitleDurationPerCharMs,
            subtitleMinDurationMs
        );
        subtitleTimeout = setTimeout(showNextSubtitleSegment, duration);
        return;
    }

    // 还有不足 28 字、也没有标点的流式片段时，先短暂等待后续内容；
    // 如果确实没有后续内容，再强制作为一段显示。
    const delay = subtitleBuffer.length > 0
        ? subtitlePendingFlushTimeoutMs
        : subtitleIdleTimeoutMs;
    subtitleIdleTimer = setTimeout(() => {
        subtitleIdleTimer = null;
        if (enqueuePendingSubtitle(true)) {
            showNextSubtitleSegment();
            return;
        }

        if (subtitleQueue.length > 0) {
            showNextSubtitleSegment();
            return;
        }

        if (subtitleHideQueued) {
            subtitleHideQueued = false;
        }
        hideSubtitleNow();
    }, delay);
}

function advanceSubtitle() {
    if (subtitleTimeout !== null) {
        clearTimeout(subtitleTimeout);
        subtitleTimeout = null;
    }
    if (subtitleIdleTimer !== null) {
        clearTimeout(subtitleIdleTimer);
        subtitleIdleTimer = null;
    }

    if (subtitleBuffer.length > 0) {
        enqueuePendingSubtitle(true);
    }

    if (subtitleQueue.length > 0) {
        showNextSubtitleSegment();
        return;
    }

    subtitleHideQueued = false;
    hideSubtitleNow();
}

function showSubtitle(text) {

    if (!text || !text.trim()) {
        return;
    }

    if (subtitleIdleTimer !== null) {
        clearTimeout(subtitleIdleTimer);
        subtitleIdleTimer = null;
    }

    // 新内容到达说明上一条隐藏标记已过期。
    subtitleHideQueued = false;

    const value = text.trim();

    // 必须在重新显示气泡前判断“上一条是否已经隐藏”，
    // 否则新的对话会沿用上一次的字数/时长统计。
    if (
        subtitleTimeout === null &&
        subtitleIdleTimer === null &&
        subtitleQueue.length === 0 &&
        !subtitleEl.classList.contains("visible")
    ) {
        conversationStartedAt = Date.now();
        conversationCharCount = 0;
        conversationSegmentShown = 0;
        conversationMotionTriggered = false;
    }
    if (conversationStartedAt === 0) {
        conversationStartedAt = Date.now();
    }

    subtitleBuffer += value;
    conversationCharCount += Array.from(value).length;
    enqueuePendingSubtitle(false);

    if (subtitleTimeout === null) {
        showNextSubtitleSegment();
    }
}

function hideSubtitle() {
    // 收到 Closing 时，先把仍在等待标点的缓冲内容显示完，再排队隐藏。
    enqueuePendingSubtitle(true);

    if (subtitleTimeout !== null || subtitleQueue.length > 0) {
        subtitleHideQueued = true;
        return;
    }

    if (subtitleIdleTimer !== null) {
        clearTimeout(subtitleIdleTimer);
        subtitleIdleTimer = null;
    }
    hideSubtitleNow();
}

function postMessage(data) {
    try {
        ipcRenderer.send(petChannel, JSON.stringify(data));
    } catch (err) {
        console.error('发送 IPC 消息失败:', err);
    }
}

// ---- 拖拽移动动画（行不变时不重置帧，避免看起来只有一帧） ----
let dragIdleTimer = null;

function setDragMotion(direction) {
    const targetRow = direction === 'right' ? ROW_RUNNING_RIGHT : ROW_RUNNING_LEFT;
    if (currentRow !== targetRow) {
        playMotion(targetRow, true);
    } else {
        clearMotionEndTimer();
    }

    clearTimeout(dragIdleTimer);
    dragIdleTimer = setTimeout(() => {
        playMotion(ROW_IDLE, true);
    }, 250);
}

// ---- 点击随机动画 / 双击打断 ----
function playRandomMotion(row, durationMs) {
    clearTimeout(dragIdleTimer);
    const loop = (row === 6 || row === 7 || row === 8);
    playMotion(row, loop, durationMs);
    randomMotionCooldownUntil = Date.now() + durationMs + 1000;
}

function interruptPlayback() {
    clearSubtitlePlayback();
    hideSubtitleNow();
    clearMotionEndTimer();
    clearTimeout(dragIdleTimer);
    dragIdleTimer = null;
    randomMotionCooldownUntil = 0;
    playMotion(ROW_IDLE, true);
}

// ---- 鼠标拖拽 + 点击检测 ----
let isMouseDown = false;
let mouseDownScreenX = 0;
let mouseDownScreenY = 0;
let mouseDownTime = 0;

window.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;
    isMouseDown = true;
    mouseDownScreenX = e.screenX;
    mouseDownScreenY = e.screenY;
    mouseDownTime = Date.now();
    postMessage({ type: 'grabStart' });
});

window.addEventListener('mouseup', (e) => {
    if (!isMouseDown) return;
    isMouseDown = false;

    const dist = Math.hypot(e.screenX - mouseDownScreenX, e.screenY - mouseDownScreenY);
    const duration = Date.now() - mouseDownTime;

    // 位移很小且时间短 → 视为点击；惊喜动作期间及结束后 1 秒内不触发
    if (dist < 6 && duration < 500 && Date.now() >= randomMotionCooldownUntil) {
        postMessage({ type: 'petClick' });
    }

    postMessage({ type: 'grabEnd' });
});

window.addEventListener('mouseleave', () => {
    if (isMouseDown) {
        isMouseDown = false;
        postMessage({ type: 'grabEnd' });
    }
});

window.addEventListener('dblclick', (e) => {
    if (e.button !== 0) return;
    postMessage({ type: 'petInterrupt' });
});

ipcRenderer.on(petChannel, (event, message) => {
    try {
        const data = typeof message === 'string' ? JSON.parse(message) : message;

        switch (data.type) {
            case 'load':
                loadSpritesheet(data.url)
                    .then(() => {
                        // 只有新精灵图加载成功后才切换 v1/v2 行数，避免热切换失败后旧图按错误行数绘制。
                        setAtlasInfo(data.version, data.rows);
                        resizeCanvas();
                        postMessage({ type: 'loaded' });
                        playMotion(ROW_IDLE, true);
                    })
                    .catch(err => {
                        postMessage({ type: 'error', message: err.message });
                    });
                break;

            case 'setPetArea':
                petAreaHeight = Number.isFinite(data.height) ? data.height : 0;
                resizeCanvas();
                break;

            case 'playMotion':
                playMotion(data.row, data.loop);
                break;

            case 'setLookDirection':
                setLookDirection(data.frame);
                break;

            case 'clearLookDirection':
                clearLookDirection();
                break;

            case 'setUsing':
                setUsing(data.isUsing);
                break;

            case 'setAnimationSpeed':
                setAnimationSpeed(data.speed);
                break;

            case 'setBubbleStyle':
                setBubbleStyle(data.fontSize, data.scale);
                break;

            case 'setVisualStyle':
                setVisualStyle(data.glow, data.outline, data.outlineColor);
                break;

            case 'setMouseLookEnabled':
                setMouseLookEnabled(data.enabled);
                break;

            case 'setMouseLookFrame':
                setMouseLookFrame(data.frame);
                break;

            case 'setDragMotion':
                setDragMotion(data.direction);
                break;

            case 'playRandomMotion':
                playRandomMotion(data.row, data.durationMs);
                break;

            case 'interruptPlayback':
                interruptPlayback();
                break;

            case 'advanceSubtitle':
                advanceSubtitle();
                break;

            case 'showSubtitle':
                showSubtitle(data.text);
                break;

            case 'hideSubtitle':
                // 忽略主进程的提前隐藏（语音流程），气泡由本地定时器控制消失
                break;
        }
    } catch (err) {
        console.error('处理 IPC 消息失败:', err);
    }
});

window.addEventListener('resize', resizeCanvas);
resizeCanvas();
requestAnimationFrame(draw);
requestAnimationFrame(animate);

postMessage({ type: 'ready' });


