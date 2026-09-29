const demoFrame = document.querySelector("#demo-frame");
const demoTitle = document.querySelector("#demo-title");
const demoDescription = document.querySelector("#demo-description");
const openDemo = document.querySelector("#open-demo");
const sourceName = document.querySelector("#source-name");
const sourceCode = document.querySelector("#source-code");
const copySource = document.querySelector("#copy-source");
const demoTabs = [...document.querySelectorAll(".demo-tab")];

let currentSource = "";

async function loadSource(name) {
  const file = `${name}.html`;
  const path = `yio-demo/ui/xingque/${file}`;
  sourceName.textContent = file;
  sourceCode.textContent = "正在读取 Yio 源文件…";

  try {
    const response = await fetch(path);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const text = await response.text();
    const lines = text.split("\n");
    currentSource = lines.slice(0, 52).join("\n");
    sourceCode.textContent = currentSource;
  } catch (error) {
    currentSource = "";
    sourceCode.textContent = "源码读取需要通过 HTTP 服务访问。请按 README 启动本地静态服务器。";
  }
}

function selectDemo(button) {
  const name = button.dataset.demo;
  const file = `${name}.html`;
  const path = `yio-demo/ui/xingque/${file}`;

  demoTabs.forEach((tab) => tab.classList.toggle("is-active", tab === button));
  demoFrame.src = path;
  demoFrame.title = `星阙行纪${button.dataset.title}`;
  demoTitle.textContent = button.dataset.title;
  demoDescription.textContent = button.dataset.description;
  if (openDemo) openDemo.href = path;
  loadSource(name);
}

demoTabs.forEach((button) => {
  button.addEventListener("click", () => selectDemo(button));
});

copySource.addEventListener("click", async () => {
  if (!currentSource) return;
  await navigator.clipboard.writeText(currentSource);
  const original = copySource.textContent;
  copySource.textContent = "已复制";
  window.setTimeout(() => {
    copySource.textContent = original;
  }, 1400);
});

const revealObserver = new IntersectionObserver((entries) => {
  entries.forEach((entry) => {
    if (entry.isIntersecting) {
      entry.target.classList.add("is-visible");
      revealObserver.unobserve(entry.target);
    }
  });
}, { threshold: 0.12 });

document.querySelectorAll(".reveal").forEach((element) => revealObserver.observe(element));
loadSource("main");
