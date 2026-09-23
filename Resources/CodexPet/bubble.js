const { ipcRenderer } = require('electron');

const subtitleEl = document.getElementById('subtitle');
const params = new URLSearchParams(window.location.search);
const petChannel = params.get('petChannel');

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
let bubbleVisible = false;
const subtitleQueue = [];
let subtitleBuffer = "";

function postMessage(data) {
    try {
        ipcRenderer.send(petChannel, JSON.stringify(data));
    } catch (err) {
        console.error('发送 IPC 消息失败:', err);
    }
}

function setBubbleVisible(visible) {
    if (bubbleVisible === visible)
        return;
    bubbleVisible = visible;
    postMessage({ type: visible ? 'bubbleShow' : 'bubbleHide' });
}

function findSubtitleBreakIndex(chars) {
    if (chars.length <= subtitleSegmentLength)
        return chars.length;

    for (let i = subtitleSegmentLength - 1; i >= 0; i--) {
        if (subtitleSentenceMarks.has(chars[i]))
            return i + 1;
    }

    for (let i = subtitleSegmentLength - 1; i >= 0; i--) {
        if (subtitlePauseMarks.has(chars[i]))
            return i + 1;
    }

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

function enqueueBlock(text) {
    let chars = Array.from(text);
    while (chars.length > 0) {
        if (chars.length > subtitleSegmentLength) {
            const take = findSubtitleBreakIndex(chars);
            subtitleQueue.push(chars.slice(0, take).join(""));
            chars = chars.slice(take);
            continue;
        }
        subtitleQueue.push(chars.join(""));
        chars = [];
    }
}
function hideSubtitleNow() {
    subtitleEl.classList.remove("visible");
    subtitleEl.textContent = "";
    subtitleBuffer = "";
    setBubbleVisible(false);
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
        setBubbleVisible(true);

        const duration = Math.max(
            segment.length * subtitleDurationPerCharMs,
            subtitleMinDurationMs
        );
        subtitleTimeout = setTimeout(showNextSubtitleSegment, duration);
        return;
    }

    // 说完台词 C# 已请求隐藏：队列一播完立刻隐藏，不再傻等空闲计时器。
    if (subtitleHideQueued) {
        subtitleHideQueued = false;
        hideSubtitleNow();
        return;
    }

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

        if (subtitleHideQueued)
            subtitleHideQueued = false;
        hideSubtitleNow();
    }, delay);
}

function showSubtitle(text) {
    if (!text || !text.trim())
        return;

    if (subtitleIdleTimer !== null) {
        clearTimeout(subtitleIdleTimer);
        subtitleIdleTimer = null;
    }

    subtitleHideQueued = false;
    // 每个 Speak 块到达即完整分段入队：块间不粘连、不等标点，
    // 已送达的回复内容全部进入队列，点击即可连续跳过。
    enqueueBlock(text.trim());

    if (subtitleTimeout === null)
        showNextSubtitleSegment();
}

function hideSubtitle() {
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

function advanceSubtitle() {
    if (subtitleTimeout !== null) {
        clearTimeout(subtitleTimeout);
        subtitleTimeout = null;
    }
    if (subtitleIdleTimer !== null) {
        clearTimeout(subtitleIdleTimer);
        subtitleIdleTimer = null;
    }

    if (subtitleBuffer.length > 0)
        enqueuePendingSubtitle(true);

    if (subtitleQueue.length > 0) {
        showNextSubtitleSegment();
        return;
    }

    // 队列已空：保持气泡可见，下一块文本到达即自动顶上。
    subtitleHideQueued = false;
}

function interruptPlayback() {
    clearSubtitlePlayback();
    hideSubtitleNow();
}

function setBubbleStyle(fontSize, scale) {
    const safeFontSize = Math.min(20, Math.max(10, Number(fontSize) || 13));
    const safeScale = Math.min(2, Math.max(0.6, Number(scale) || 0.85));
    document.documentElement.style.setProperty("--bubble-font-size", safeFontSize + "px");
    document.documentElement.style.setProperty("--bubble-scale", String(safeScale));
}

subtitleEl.addEventListener('mousedown', e => e.stopPropagation());
subtitleEl.addEventListener('mouseup', e => e.stopPropagation());
subtitleEl.addEventListener('dblclick', e => e.stopPropagation());
// 整个气泡窗口任意位置点击都跳到下一段（透明区域系统级穿透，点到的一定是气泡内容）。
window.addEventListener('click', advanceSubtitle);

ipcRenderer.on(petChannel, (event, message) => {
    try {
        const data = typeof message === 'string' ? JSON.parse(message) : message;

        switch (data.type) {
            case 'setBubbleStyle':
                setBubbleStyle(data.fontSize, data.scale);
                break;

            case 'showSubtitle':
                showSubtitle(data.text);
                break;

            case 'hideSubtitle':
                hideSubtitle();
                break;

            case 'advanceSubtitle':
                advanceSubtitle();
                break;

            case 'interruptPlayback':
                interruptPlayback();
                break;
        }
    } catch (err) {
        console.error('处理 IPC 消息失败:', err);
    }
});
