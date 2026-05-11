const RESULT_PENDING = "-";
const RESULT_MATCH = "○";
const RESULT_MISMATCH = "✖";
const STATUS_WAITING = "照合待ち";
const STATUS_REGISTERED = "マスターデータ登録済み";
const STATUS_MATCH = "照合成功";
const STATUS_MISMATCH = "照合失敗";
const DEFAULT_LOG_FILE_NAME = "PY000000";
const STORAGE_KEY = "ppy-web-state";
const HONEYWELL_READER_OK = 0;

const state = {
  screen: "menu",
  selectedMode: null,
  masterInput: "",
  masterData: "",
  slaveData: "",
  resultSymbol: RESULT_PENDING,
  resultMessage: STATUS_WAITING,
  matchCount: 0,
  masterStartPosition: "1",
  slaveStartPosition: "1",
  comparisonDigits: "0",
  logFileNameSetting: DEFAULT_LOG_FILE_NAME,
  logs: [],
  isSoftwareKeyboardVisible: false,
  activeInputTarget: "master"
};

let honeywellBarcodeReader = null;
let honeywellBarcodeReaderOpening = false;

const elements = {
  menuScreen: document.querySelector("#menu-screen"),
  launchPyButton: document.querySelector("#launch-py-button"),
  verificationScreen: document.querySelector("#verification-screen"),
  settingsScreen: document.querySelector("#settings-screen"),
  modeButtons: document.querySelectorAll(".mode-button"),
  modeMessage: document.querySelector("#mode-message"),
  logMessage: document.querySelector("#log-message"),
  matchCount: document.querySelector("#match-count"),
  settingsMatchCount: document.querySelector("#settings-match-count"),
  resultSymbol: document.querySelector("#result-symbol"),
  openSettings: document.querySelector("#open-settings"),
  backButton: document.querySelector("#back-button"),
  masterInput: document.querySelector("#master-input"),
  slaveInput: document.querySelector("#slave-input"),
  scannerCaptureInput: document.querySelector("#scanner-capture-input"),
  keyboardFocusSink: document.querySelector("#keyboard-focus-sink"),
  clearButton: document.querySelector("#clear-button"),
  retryButton: document.querySelector("#retry-button"),
  keyboardToggleButton: document.querySelector("#keyboard-toggle-button"),
  masterStart: document.querySelector("#master-start"),
  slaveStart: document.querySelector("#slave-start"),
  comparisonDigits: document.querySelector("#comparison-digits"),
  logFileName: document.querySelector("#log-file-name"),
  clearCountButton: document.querySelector("#clear-count-button"),
  downloadLogButton: document.querySelector("#download-log-button"),
  deleteLogButton: document.querySelector("#delete-log-button"),
  dialog: document.querySelector("#confirm-dialog"),
  dialogMessage: document.querySelector("#dialog-message"),
  dialogOk: document.querySelector("#dialog-ok")
};

function loadState() {
  const stored = localStorage.getItem(STORAGE_KEY);
  if (!stored) return;

  try {
    const parsed = JSON.parse(stored);
    Object.assign(state, {
      matchCount: Number(parsed.matchCount) || 0,
      masterStartPosition: parsed.masterStartPosition || "1",
      slaveStartPosition: parsed.slaveStartPosition || "1",
      comparisonDigits: parsed.comparisonDigits || "0",
      logFileNameSetting: parsed.logFileNameSetting || DEFAULT_LOG_FILE_NAME,
      logs: Array.isArray(parsed.logs) ? parsed.logs : []
    });
  } catch (error) {
    localStorage.removeItem(STORAGE_KEY);
  }
}

function saveState() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify({
    matchCount: state.matchCount,
    masterStartPosition: state.masterStartPosition,
    slaveStartPosition: state.slaveStartPosition,
    comparisonDigits: state.comparisonDigits,
    logFileNameSetting: state.logFileNameSetting,
    logs: state.logs
  }));
}

function sanitizeScanInput(value) {
  return value.replace(/\r/g, "").replace(/\n/g, "");
}

function hasScanTerminator(value) {
  return value.includes("\n") || value.includes("\r");
}

function digitsOnly(value) {
  return value.replace(/\D/g, "");
}

function extractComparableSegment(value, startPositionText, digitsText) {
  const startIndex = Math.max((Number.parseInt(startPositionText, 10) || 1), 1) - 1;
  if (startIndex >= value.length) return "";

  const digits = Number.parseInt(digitsText, 10) || 0;
  return digits <= 0 ? value.slice(startIndex) : value.slice(startIndex, startIndex + digits);
}

function buildVerificationLogLine(masterData, slaveData, resultSymbol) {
  const now = new Date();
  const timestamp = [
    now.getFullYear(),
    String(now.getMonth() + 1).padStart(2, "0"),
    String(now.getDate()).padStart(2, "0")
  ].join("/") + " " + [
    String(now.getHours()).padStart(2, "0"),
    String(now.getMinutes()).padStart(2, "0"),
    String(now.getSeconds()).padStart(2, "0")
  ].join(":");

  const resultLabel = resultSymbol === RESULT_MATCH ? "OK" : resultSymbol === RESULT_MISMATCH ? "NG" : resultSymbol;
  return `${resultLabel},${masterData},${slaveData},${timestamp}`;
}

function buildLogFileName(value) {
  const normalized = value.trim().replace(/[\\/:*?"<>|]/g, "").replace(/\.txt$/i, "");
  return `${normalized || DEFAULT_LOG_FILE_NAME}.txt`;
}

function setScreen(screen) {
  state.screen = screen;
  if (screen !== "verification") {
    state.isSoftwareKeyboardVisible = false;
  }
  elements.menuScreen.classList.toggle("is-active", screen === "menu");
  elements.verificationScreen.classList.toggle("is-active", screen === "verification");
  elements.settingsScreen.classList.toggle("is-active", screen === "settings");

  if (screen === "verification") {
    openHoneywellBarcodeReader();
    render();
    if (inputEnabled()) {
      restoreScannerInputSilently();
    }
  }
}

function launchPyApp() {
  openHoneywellBarcodeReader();
  setScreen("verification");
}

function playTone(isMatch) {
  const AudioContext = window.AudioContext || window.webkitAudioContext;
  if (!AudioContext) return;

  const context = new AudioContext();
  const oscillator = context.createOscillator();
  const gain = context.createGain();
  oscillator.type = isMatch ? "sine" : "square";
  oscillator.frequency.value = isMatch ? 880 : 180;
  gain.gain.setValueAtTime(0.08, context.currentTime);
  gain.gain.exponentialRampToValueAtTime(0.001, context.currentTime + (isMatch ? 0.18 : 0.3));
  oscillator.connect(gain).connect(context.destination);
  oscillator.start();
  oscillator.stop(context.currentTime + (isMatch ? 0.18 : 0.3));
}

function appendVerificationLog(masterData, slaveData, resultSymbol) {
  try {
    state.logs.push(buildVerificationLogLine(masterData, slaveData, resultSymbol));
    saveState();
    elements.logMessage.hidden = true;
  } catch (error) {
    elements.logMessage.hidden = false;
  }
}

function handleHoneywellBarcodeData(data) {
  let rawData = data;

  if (data && typeof data === "object") {
    if (typeof data.data !== "undefined") {
      rawData = data.data;
    } else if (data.barcode && typeof data.barcode.data !== "undefined") {
      rawData = data.barcode.data;
    } else if (data.detail && typeof data.detail.data !== "undefined") {
      rawData = data.detail.data;
    }
  }

  const scanData = sanitizeScanInput(String(rawData || ""));
  if (!scanData || !inputEnabled()) return;

  if (state.activeInputTarget === "slave" || state.masterData) {
    state.slaveData = scanData;
    render();
    submitSlaveData(scanData);
    return;
  }

  state.masterInput = scanData;
  render();
  registerMasterData(scanData);
}

function handleBarcodeTextData(text) {
  const scanData = sanitizeScanInput(String(text || ""));
  if (!scanData || !inputEnabled()) return;
  handleHoneywellBarcodeData(scanData);
}

function onHoneywellBarcodeReaderComplete(result) {
  honeywellBarcodeReaderOpening = false;

  if (!result || result.status !== HONEYWELL_READER_OK) {
    honeywellBarcodeReader = null;
    return;
  }

  honeywellBarcodeReader.setBuffered("Symbology", "Code39", "Enable", "true", function () {});
  honeywellBarcodeReader.setBuffered("Symbology", "Code128", "EnableCode128", "true", function () {});
  honeywellBarcodeReader.setBuffered("Symbology", "EANUPC", "EnableEAN13", "true", function () {});
  honeywellBarcodeReader.setBuffered("Symbology", "QRCode", "Enable", "true", function () {});
  honeywellBarcodeReader.commitBuffer(function () {});
  honeywellBarcodeReader.addEventListener("barcodedataready", handleHoneywellBarcodeData, false);
  enableHoneywellBarcodeTrigger();
}

function openHoneywellBarcodeReader() {
  if (honeywellBarcodeReader || honeywellBarcodeReaderOpening) return;
  if (typeof BarcodeReader === "undefined") return;

  honeywellBarcodeReaderOpening = true;
  try {
    honeywellBarcodeReader = new BarcodeReader(null, onHoneywellBarcodeReaderComplete);
  } catch (error) {
    honeywellBarcodeReader = null;
    honeywellBarcodeReaderOpening = false;
  }
}

function enableHoneywellBarcodeTrigger() {
  if (!honeywellBarcodeReader || !honeywellBarcodeReader.enableTrigger) return;

  try {
    honeywellBarcodeReader.enableTrigger(true, function () {});
  } catch (error) {
    // Trigger control is best effort; barcode events may still arrive on some devices.
  }
}

function closeHoneywellBarcodeReader() {
  if (!honeywellBarcodeReader) return;

  try {
    honeywellBarcodeReader.close(function () {
      honeywellBarcodeReader = null;
      honeywellBarcodeReaderOpening = false;
    });
  } catch (error) {
    honeywellBarcodeReader = null;
    honeywellBarcodeReaderOpening = false;
  }
}

function applyKeyboardMode(input, isVisible) {
  input.inputMode = "text";
  input.setAttribute("inputmode", "text");
  input.setAttribute("virtualkeyboardpolicy", isVisible ? "auto" : "manual");
}

function suppressKeyboardForVisibleInputs() {
  [elements.masterInput, elements.slaveInput, elements.scannerCaptureInput].forEach((input) => {
    input.readOnly = true;
    input.inputMode = "none";
    input.setAttribute("inputmode", "none");
    input.setAttribute("virtualkeyboardpolicy", "manual");
  });
}

function moveFocusAwayFromInputs() {
  [
    elements.masterInput,
    elements.slaveInput,
    elements.scannerCaptureInput
  ].forEach((input) => {
    input.blur();
  });

  try {
    elements.keyboardFocusSink.focus({ preventScroll: true });
  } catch (error) {
    elements.keyboardFocusSink.focus();
  }
}

function focusInput(input, options = {}) {
  const { showKeyboard = state.isSoftwareKeyboardVisible, forceRefocus = false } = options;
  applyKeyboardMode(input, showKeyboard);
  input.disabled = false;

  if (document.activeElement && document.activeElement !== input && document.activeElement.blur) {
    document.activeElement.blur();
  }

  if (forceRefocus) {
    input.blur();
  }

  try {
    input.focus({ preventScroll: true });
  } catch (error) {
    input.focus();
  }
  input.setSelectionRange(input.value.length, input.value.length);
}

function focusScannerCapture(options = {}) {
  if (!inputEnabled()) return;

  const { forceRefocus = false } = options;
  const input = elements.scannerCaptureInput;
  applyKeyboardMode(input, false);
  input.disabled = false;
  input.readOnly = false;
  input.value = "";

  if (document.activeElement && document.activeElement !== input && document.activeElement.blur) {
    document.activeElement.blur();
  }

  if (forceRefocus) {
    input.blur();
  }

  try {
    input.focus({ preventScroll: true });
  } catch (error) {
    input.focus();
  }
}

function focusNext(options = {}) {
  if (!state.selectedMode || state.resultSymbol === RESULT_MISMATCH) return;

  const focus = () => {
    if (state.masterData) {
      state.activeInputTarget = "slave";
      focusInput(elements.slaveInput, options);
    } else {
      state.activeInputTarget = "master";
      focusInput(elements.masterInput, options);
    }
  };

  if (options.defer) {
    requestAnimationFrame(focus);
  } else {
    focus();
  }
}

function hideVirtualKeyboard() {
  if (navigator.virtualKeyboard && navigator.virtualKeyboard.hide) {
    try {
      navigator.virtualKeyboard.hide();
    } catch (error) {
      // Some Android browsers expose the API but reject programmatic hide.
    }
  }
}

function primeScannerInput() {
  if (!inputEnabled()) return;

  state.isSoftwareKeyboardVisible = false;
  focusNext({ showKeyboard: true, forceRefocus: true });
  hideVirtualKeyboard();

  const settleScannerFocus = () => {
    state.isSoftwareKeyboardVisible = false;
    render();
    focusScannerCapture({ forceRefocus: true });
    hideVirtualKeyboard();
  };

  window.setTimeout(settleScannerFocus, 80);
  window.setTimeout(settleScannerFocus, 220);
}

function restoreScannerInputSilently() {
  if (!inputEnabled()) return;

  state.isSoftwareKeyboardVisible = false;
  render();
  focusScannerCapture({ forceRefocus: true });
  hideVirtualKeyboard();

  window.setTimeout(() => {
    render();
    focusScannerCapture({ forceRefocus: true });
    hideVirtualKeyboard();
  }, 120);
}

function hideSoftwareKeyboardAndPrimeScanner() {
  state.isSoftwareKeyboardVisible = false;

  suppressKeyboardForVisibleInputs();
  moveFocusAwayFromInputs();
  hideVirtualKeyboard();

  window.setTimeout(() => {
    render();
    moveFocusAwayFromInputs();
    hideVirtualKeyboard();
  }, 160);
}

function inputEnabled() {
  return state.selectedMode !== null && state.resultSymbol !== RESULT_MISMATCH;
}

function targetInput() {
  return state.activeInputTarget === "slave" ? elements.slaveInput : elements.masterInput;
}

function setTargetValue(value) {
  if (state.activeInputTarget === "slave") {
    state.slaveData = value;
  } else {
    state.masterInput = value;
  }
}

function submitActiveInput() {
  if (state.activeInputTarget === "slave") {
    submitSlaveData(state.slaveData);
  } else {
    registerMasterData();
  }
}

function receiveTextInput(text) {
  if (!inputEnabled() || !text) return;

  const sanitizedValue = sanitizeScanInput(text);
  setTargetValue(targetInput().value + sanitizedValue);
  render();

  if (hasScanTerminator(text)) {
    submitActiveInput();
  }
}

function toggleSoftwareKeyboard() {
  if (!inputEnabled()) return;

  if (state.isSoftwareKeyboardVisible) {
    hideSoftwareKeyboardAndPrimeScanner();
    return;
  }

  state.isSoftwareKeyboardVisible = !state.isSoftwareKeyboardVisible;
  render();
  focusInput(targetInput(), {
    showKeyboard: state.isSoftwareKeyboardVisible,
    forceRefocus: true
  });
}

function render() {
  const isInputEnabled = inputEnabled();
  const settingsEnabled = state.masterData.length === 0;

  elements.modeButtons.forEach((button) => {
    const isSelected = button.dataset.mode === state.selectedMode;
    button.classList.toggle("is-selected", isSelected);
    button.disabled = state.resultSymbol === RESULT_MISMATCH;
  });

  elements.modeMessage.textContent = state.selectedMode ? state.resultMessage : "照合モードを選択してください";
  elements.matchCount.textContent = `${state.matchCount}件`;
  elements.settingsMatchCount.textContent = `${state.matchCount}件`;
  elements.resultSymbol.textContent = state.resultSymbol;
  elements.resultSymbol.className = "result-symbol " + (
    state.resultSymbol === RESULT_MATCH ? "ok" : state.resultSymbol === RESULT_MISMATCH ? "ng" : "pending"
  );

  elements.openSettings.disabled = !settingsEnabled;
  elements.masterInput.disabled = !isInputEnabled;
  elements.slaveInput.disabled = !isInputEnabled;
  elements.masterInput.readOnly = !state.isSoftwareKeyboardVisible;
  elements.slaveInput.readOnly = !state.isSoftwareKeyboardVisible;
  if (state.isSoftwareKeyboardVisible) {
    applyKeyboardMode(elements.masterInput, true);
    applyKeyboardMode(elements.slaveInput, true);
  } else {
    suppressKeyboardForVisibleInputs();
  }
  elements.masterInput.value = state.masterInput;
  elements.slaveInput.value = state.slaveData;
  elements.keyboardToggleButton.disabled = !isInputEnabled;
  elements.keyboardToggleButton.textContent = state.isSoftwareKeyboardVisible ? "キーボード非表示" : "キーボード表示";
  elements.masterStart.value = state.masterStartPosition;
  elements.slaveStart.value = state.slaveStartPosition;
  elements.comparisonDigits.value = state.comparisonDigits;
  elements.logFileName.value = state.logFileNameSetting;
}

function selectMode(mode) {
  state.selectedMode = mode;
  state.resultMessage = state.masterData ? STATUS_REGISTERED : STATUS_WAITING;
  state.isSoftwareKeyboardVisible = false;
  state.activeInputTarget = state.masterData ? "slave" : "master";
  render();
  openHoneywellBarcodeReader();
  enableHoneywellBarcodeTrigger();
  primeScannerInput();
  window.setTimeout(() => {
    restoreScannerInputSilently();
  }, 320);
}

function registerMasterData(value = state.masterInput) {
  if (!value) return;
  state.masterData = value;
  state.resultSymbol = RESULT_PENDING;
  state.resultMessage = STATUS_REGISTERED;
  render();
  focusNext({ showKeyboard: false, forceRefocus: true });
}

function submitSlaveData(value) {
  if (!value) return;
  compareData(value);
}

function compareData(currentSlaveData) {
  if (!state.masterData || !currentSlaveData) return;

  const comparableMasterData = extractComparableSegment(
    state.masterData,
    state.masterStartPosition,
    state.comparisonDigits
  );
  const comparableSlaveData = extractComparableSegment(
    currentSlaveData,
    state.slaveStartPosition,
    state.comparisonDigits
  );
  const isMatch = comparableMasterData !== "" && comparableSlaveData !== "" && comparableMasterData === comparableSlaveData;
  const comparisonResult = isMatch ? RESULT_MATCH : RESULT_MISMATCH;

  state.resultSymbol = comparisonResult;
  state.resultMessage = isMatch ? STATUS_MATCH : STATUS_MISMATCH;
  if (isMatch) state.matchCount += 1;
  appendVerificationLog(state.masterData, currentSlaveData, comparisonResult);
  playTone(isMatch);

  if (state.selectedMode === "one-to-many" && isMatch) {
    state.slaveData = "";
  } else if (isMatch) {
    state.masterInput = "";
    state.masterData = "";
    state.slaveData = "";
  }

  saveState();
  render();
  focusNext({ showKeyboard: false, forceRefocus: true });
}

function clearVerification() {
  state.masterInput = "";
  state.masterData = "";
  state.slaveData = "";
  state.resultSymbol = RESULT_PENDING;
  state.resultMessage = STATUS_WAITING;
  state.activeInputTarget = "master";
  render();
  restoreScannerInputSilently();
}

function retryInput() {
  if (state.resultSymbol === RESULT_MISMATCH) {
    state.slaveData = "";
    state.resultSymbol = RESULT_PENDING;
    state.resultMessage = STATUS_REGISTERED;
    state.activeInputTarget = "slave";
  } else {
    state.activeInputTarget = state.masterData ? "slave" : "master";
  }
  render();
  restoreScannerInputSilently();
}

function confirmAction(message, onConfirm) {
  elements.dialogMessage.textContent = message;
  elements.dialogOk.onclick = () => onConfirm();
  elements.dialog.showModal();
}

function downloadLog() {
  const content = state.logs.length ? `${state.logs.join("\n")}\n` : "";
  const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = buildLogFileName(state.logFileNameSetting);
  link.click();
  URL.revokeObjectURL(url);
}

function bindScannerActionButton(button, action) {
  let handledPointerAction = false;
  let handledTouchAction = false;
  let handledMouseAction = false;

  button.setAttribute("tabindex", "-1");

  button.addEventListener("touchstart", (event) => {
    event.preventDefault();
  }, { passive: false });

  button.addEventListener("touchend", (event) => {
    event.preventDefault();
    handledTouchAction = true;
    action();
    window.setTimeout(() => {
      handledTouchAction = false;
    }, 0);
  }, { passive: false });

  button.addEventListener("mousedown", (event) => {
    event.preventDefault();
  });

  button.addEventListener("mouseup", (event) => {
    if (handledTouchAction || handledPointerAction) return;
    event.preventDefault();
    handledMouseAction = true;
    action();
    window.setTimeout(() => {
      handledMouseAction = false;
    }, 0);
  });

  button.addEventListener("pointerdown", (event) => {
    event.preventDefault();
  });

  button.addEventListener("pointerup", (event) => {
    if (handledTouchAction || handledMouseAction) return;
    event.preventDefault();
    handledPointerAction = true;
    action();
    window.setTimeout(() => {
      handledPointerAction = false;
    }, 0);
  });

  button.addEventListener("click", () => {
    if (handledTouchAction || handledMouseAction) return;
    if (handledPointerAction) return;
    action();
  });
}

elements.modeButtons.forEach((button) => {
  bindScannerActionButton(button, () => selectMode(button.dataset.mode));
});

elements.openSettings.addEventListener("click", () => setScreen("settings"));
elements.backButton.addEventListener("click", () => setScreen("verification"));
elements.launchPyButton.addEventListener("click", launchPyApp);
bindScannerActionButton(elements.clearButton, clearVerification);
bindScannerActionButton(elements.retryButton, retryInput);
elements.keyboardToggleButton.addEventListener("click", toggleSoftwareKeyboard);

elements.masterInput.addEventListener("focus", () => {
  state.activeInputTarget = "master";
});

elements.masterInput.addEventListener("pointerdown", () => {
  state.activeInputTarget = "master";
});

elements.masterInput.addEventListener("input", (event) => {
  const rawValue = event.target.value;
  const sanitizedValue = sanitizeScanInput(rawValue);
  state.masterInput = sanitizedValue;
  if (hasScanTerminator(rawValue) && sanitizedValue) {
    registerMasterData(sanitizedValue);
  }
  render();
});

elements.masterInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    event.preventDefault();
    registerMasterData();
  }
});

elements.slaveInput.addEventListener("focus", () => {
  state.activeInputTarget = "slave";
});

elements.slaveInput.addEventListener("pointerdown", () => {
  state.activeInputTarget = "slave";
});

elements.slaveInput.addEventListener("input", (event) => {
  const rawValue = event.target.value;
  const sanitizedValue = sanitizeScanInput(rawValue);
  state.slaveData = sanitizedValue;
  if (hasScanTerminator(rawValue) && sanitizedValue) {
    submitSlaveData(sanitizedValue);
  }
  render();
});

elements.slaveInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    event.preventDefault();
    submitSlaveData(state.slaveData);
  }
});

elements.scannerCaptureInput.addEventListener("input", (event) => {
  const rawValue = event.target.value;
  if (!rawValue) return;

  event.target.value = "";
  handleBarcodeTextData(rawValue);
});

elements.scannerCaptureInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter") {
    event.preventDefault();
    submitActiveInput();
  }
});

document.addEventListener("keydown", (event) => {
  if (state.screen !== "verification" || state.isSoftwareKeyboardVisible || !inputEnabled()) return;
  if (event.ctrlKey || event.altKey || event.metaKey) return;
  if (
    event.target === elements.masterInput ||
    event.target === elements.slaveInput ||
    event.target === elements.scannerCaptureInput
  ) return;

  if (event.key === "Enter") {
    event.preventDefault();
    submitActiveInput();
    return;
  }

  if (event.key === "Backspace") {
    event.preventDefault();
    const currentValue = targetInput().value;
    setTargetValue(currentValue.slice(0, -1));
    render();
    return;
  }

  if (event.key.length === 1) {
    event.preventDefault();
    receiveTextInput(event.key);
  }
});

document.addEventListener("paste", (event) => {
  if (state.screen !== "verification" || state.isSoftwareKeyboardVisible || !inputEnabled()) return;
  const text = event.clipboardData ? event.clipboardData.getData("text") : "";
  if (!text) return;

  event.preventDefault();
  handleBarcodeTextData(text);
});

document.addEventListener("beforeinput", (event) => {
  if (state.screen !== "verification" || state.isSoftwareKeyboardVisible || !inputEnabled()) return;
  if (
    event.target === elements.masterInput ||
    event.target === elements.slaveInput ||
    event.target === elements.scannerCaptureInput
  ) return;
  if (event.inputType !== "insertText" || !event.data) return;

  event.preventDefault();
  receiveTextInput(event.data);
});

document.addEventListener("barcodedataready", (event) => {
  handleHoneywellBarcodeData(event);
});

elements.masterStart.addEventListener("input", (event) => {
  state.masterStartPosition = digitsOnly(event.target.value);
  saveState();
  render();
});

elements.slaveStart.addEventListener("input", (event) => {
  state.slaveStartPosition = digitsOnly(event.target.value);
  saveState();
  render();
});

elements.comparisonDigits.addEventListener("input", (event) => {
  state.comparisonDigits = digitsOnly(event.target.value);
  saveState();
  render();
});

elements.logFileName.addEventListener("input", (event) => {
  state.logFileNameSetting = event.target.value;
  elements.logMessage.hidden = true;
  saveState();
  render();
});

elements.clearCountButton.addEventListener("click", () => {
  confirmAction("照合件数をクリアしてよいですか？", () => {
    state.matchCount = 0;
    saveState();
    render();
  });
});

elements.deleteLogButton.addEventListener("click", () => {
  confirmAction("ログファイルを消去し、照合件数もクリアしてよいですか？", () => {
    state.logs = [];
    state.matchCount = 0;
    saveState();
    render();
  });
});

elements.downloadLogButton.addEventListener("click", downloadLog);

window.addEventListener("beforeunload", closeHoneywellBarcodeReader);

document.addEventListener("visibilitychange", () => {
  if (document.hidden) {
    closeHoneywellBarcodeReader();
    return;
  }

  if (state.screen === "verification") {
    openHoneywellBarcodeReader();
  }
});

loadState();
render();
