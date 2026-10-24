/**
 * Chill AI × DeepSeek Harness（dsh）联动插件 —— Host 侧、纯观测、无依赖。
 *
 * 作用：把 dsh 的真实工作状态转发给 ChillAI.Bridge（默认 http://127.0.0.1:17860/codex/events），
 * 再由 Bridge 的状态机驱动游戏女主角（工作 / 等待审批 / 完成庆祝 / 收工）。
 *
 * 为什么不用 @deepseek-ai/dsh-hooks-codex：
 *   官方桥接插件只支持 SessionStart / UserPromptSubmit / PreToolUse / PostToolUse / Stop
 *   五个 Codex 事件点（不支持 PermissionRequest），且需要往 profile 里 pnpm 安装一个
 *   与 dsh 核心版本严格对齐的 experimental 包；每次事件还要起一个 PowerShell 进程。
 *   本插件改为直接挂在 dsh 自己的拦截 seam 上，六种状态全覆盖，进程内直发 HTTP。
 *
 * 挂载点（全部是 dsh 官方文档化的 seam，见 docs/subsystems/core.zh.md 与
 * @deepseek-ai/dsh-hooks-codex 的实现）：
 *   agent/created        → SessionStart      会话开始
 *   agent/pre-step       → UserPromptSubmit  用户提交提示词（waterfall，必须委托 next）
 *   tools/post-execute   → PostToolUse       工具执行完成（waterfall，必须委托 next）
 *   approval/asked       → PermissionRequest 等待用户审批（durable session 事件）
 *   agent/turn-stopping  → Stop              轮次即将结束（串行监听器）
 *   agent/disposed       → SessionEnd        会话结束
 *
 * 设计约束（与 codex-hook.ps1 保持一致）：
 *   - 绝不阻塞 agent：HTTP 是 fire-and-forget，2 秒超时，任何失败静默吞掉；
 *   - 绝不抛异常：所有监听器内部 try/catch，Bridge 没开也照常工作；
 *   - waterfall 监听器永远 return next()，不改变任何决策（纯观测，用最弱的机制）；
 *   - 零依赖：不 import 任何 @deepseek-ai 包，只依赖 Node 内置的 fetch。
 *
 * 该文件由游戏插件（ChillAI.Plugin）自动复制到 dsh profile 目录，并在
 * cordis.patch.yml 里以相对路径 './dsh-hook.mjs' 挂载；也可手动挂载，见
 * scripts/dsh-hooks.cordis.patch.yml。
 */

/** 插件名（cordis 排障时显示）。 */
export const name = 'chillai-sync'

/** 不依赖任何服务：缺失的服务不会让插件卡在 pending。 */
export const inject = []

/** Bridge 事件上报地址；可用 CHILLAI_BRIDGE_URL 覆盖。 */
const BRIDGE_URL = process.env.CHILLAI_BRIDGE_URL || 'http://127.0.0.1:17860/codex/events'

/** 单次上报的超时（毫秒）：超时即放弃，绝不让请求堆积。 */
const REQUEST_TIMEOUT_MS = 2000

/**
 * 上报一个事件给 Bridge。fire-and-forget：不 await、不抛异常、不阻塞调用方。
 * @param {string} event Bridge 认识的事件名（与 Codex 钩子事件同名）。
 * @param {string} [detail] 可选细节（浮窗第二行显示）。
 */
function report(event, detail) {
  try {
    const payload = detail ? { event, detail: String(detail) } : { event }
    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS)
    fetch(BRIDGE_URL, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
      signal: controller.signal,
    })
      .catch(() => {})
      .finally(() => clearTimeout(timer))
  } catch {
    // Bridge 未运行 / fetch 不可用：什么都不做，让 dsh 正常继续
  }
}

/**
 * 把可能抛异常的回调包成安全的：任何异常都不允许冒泡到 dsh 的 agent 循环。
 * @param {Function} fn 被包裹的回调。
 * @returns {Function} 安全版本。
 */
function safe(fn) {
  return (...args) => {
    try {
      return fn(...args)
    } catch {
      return undefined
    }
  }
}

/**
 * 插件入口。所有注册都走 ctx.on / ctx.effect，卸载时由 cordis 自动清理。
 * @param {import('@deepseek-ai/cordis').Context} ctx 插件上下文。
 */
export function apply(ctx) {
  const log = ctx?.logger?.info?.bind(ctx.logger)
  log?.(`chillai-sync: 已挂载，事件将上报到 ${BRIDGE_URL}`)

  // ---- 会话开始 ----
  ctx.on(
    'agent/created',
    safe(({ source } = {}) => {
      report('SessionStart', source ? `dsh:${source}` : 'dsh')
    }),
  )

  // ---- 用户提交提示词（waterfall：纯观测，必须委托 next）----
  ctx.on(
    'agent/pre-step',
    safe((payload, next) => {
      // 与官方 dsh-hooks-codex 一致：只有真正带入新消息的步骤才算一次「提交」。
      if (Array.isArray(payload?.messages) && payload.messages.length > 0) {
        report('UserPromptSubmit')
      }
      return next()
    }),
  )

  // ---- 工具执行完成（waterfall：纯观测，必须委托 next）----
  ctx.on(
    'tools/post-execute',
    safe((exec, _result, next) => {
      report('PostToolUse', exec?.name)
      return next()
    }),
  )

  // ---- 轮次即将结束（串行监听器，无 next；返回 undefined 即不干预）----
  ctx.on(
    'agent/turn-stopping',
    safe(() => {
      report('Stop')
    }),
  )

  // ---- 会话结束 ----
  ctx.on(
    'agent/disposed',
    safe(() => {
      report('SessionEnd')
    }),
  )

  // ---- 等待审批：approval/asked 是 durable session 事件（没有对应的拦截 seam）----
  // 注意：session/end-seed 是 fork/resume 的种子标记，不是「会话结束」，不要误映射。
  ctx.on(
    'session/event',
    safe((_session, event) => {
      if (event?.type === 'approval/asked') {
        report('PermissionRequest', event?.data?.toolName)
      }
    }),
  )
}
