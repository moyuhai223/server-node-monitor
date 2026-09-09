# vue-naive-admin 2.x 模板研究:后端兼容契约与改造方案(R2)

> 研究对象:`web/admin`(vue-naive-admin 2.x,commit `a55f0a2e6fe0561ce62c3452b96ec4b4cbcb8553`,已去 `.git`)。
> 方法:逐文件阅读模板源码;对模板默认的 Apifox 云端 mock(`https://m1.apifoxmock.com/m1/3776410-3408296-default`)逐接口 `curl` 抓取真实响应;在本机(Win11 ARM64 VM,Node 22.22.2 / npm 10.9.7)实际 `npm install` + `npm run build`,并额外用 `VITE_PUBLIC_PATH=/admin/` 构建一次验证 `/admin/` 托管路径。
> 本文是**前端/Master 实现者的唯一契约**:第 2–11 节是模板行为的事实陈述(不可改的约束),第 12 节是推荐改造方案(定案)。文中 "mock 实测" 表示来自 Apifox 抓包;"源码推断" 表示 mock 未提供、依据模板源码反推。

---

## 0. 结论摘要

1. **模板对后端的硬依赖只有 5 个接口**:`POST /auth/login`、`GET /user/detail`、`GET /role/permissions/tree`、`GET /permission/menu/validate?path=`、`POST /auth/logout`;个人中心再加 `POST /auth/password`、`PATCH /user/profile/{id}`。其余(`/user`、`/role/*`、`/permission/*` CRUD、`/auth/current-role/switch/*`、`/auth/captcha`、`/auth/role/toggle`)只被将要删除的 pms/demo 页面调用。
2. **响应信封**:`{"code":0,"message":"OK","data":...}`;拦截器把 `code ∈ {0, 200}` 视为成功,其余(含 HTTP 4xx/5xx 的 body)一律 reject 并弹 `$message.error`;`code===401 / 11007 / 11008` 弹"是否重新登录"对话框并登出。**模板前端根本没有实现 refresh token 逻辑**(`api.refreshToken` 定义了但无人调用,mock 上 `GET /auth/refresh/token` 也是 404),README 所谓"无感刷新"是其 Nest 后端的 Redis 方案;我们要自己在拦截器加单飞刷新(见 §12.4)。
3. **菜单树即路由表**:后端返回的树中 `type=MENU` 且 `enable && path && !http` 的节点会被 `router.addRoute`,`component` 字段是 `import.meta.glob('@/views/**/*.vue')` 的 key,**必须**形如 `/src/views/xxx/index.vue`(构建产物实测 key 就是这种绝对路径)。`show:false` 的节点照常注册路由但不进侧栏,并把父级 key 作为高亮项。
4. **决策(BRIEF §3 Q6)**:**服务端兼容模板接口 + 少量前端改造**。Master 实现 §12.2 的 8 个接口(含新增的 `POST /api/auth/refresh`),前端按 §12.4 清单改约 20 个文件、删 5 组目录、新增业务页面。
5. **构建实证**:`npm install` 22 s(镜像 `registry.npmmirror.com` 正常;node_modules 903 MB / 501 个顶层包),`npm run build` 17 s 挂钟(Vite 8.2.2 / rolldown,3458 模块,97 个文件,2.48 MB,js+css gzip 约 740 KB),0 错误,仅 1 条 `configLoader: 'native'` 兼容性警告。`VITE_PUBLIC_PATH=/admin/` 构建后 `index.html` 资源前缀与 `createWebHistory('/admin/')` 均正确,**无需改任何源码**。
6. **两个本机坑**:(a) 模板 `postinstall: npx simple-git-hooks` 会把 `npx lint-staged` 写进**仓库根 `.git/hooks/pre-commit`**(本次 install 已写入;根目录无 package.json,`lint-staged` 会失败,导致 `git commit` 被挡)——必须删掉 postinstall,并由编排者清理该 hook 或用 `SKIP_SIMPLE_GIT_HOOKS=1` / `git commit --no-verify`;(b) Git Bash 会把以 `/` 开头的环境变量值做 MSYS 路径转换(`VITE_PUBLIC_PATH=/admin/` 变成 `/Program Files/Git/admin/`),脚本里通过环境变量传 Vite 值必须加 `MSYS_NO_PATHCONV=1`,或者只用 `.env` 文件(不受影响)。

---

## 1. 模板工程事实

### 1.1 版本(package.json 声明 → 本次实际安装)

| 包 | 声明 | 安装 | 备注 |
|---|---|---|---|
| vite | ^8.0.14 | 8.2.2 | 底层 rolldown;`configLoader:'native'` 警告见 §1.3 |
| vue | ^3.5.35 | 3.5.42 | |
| naive-ui | ^2.44.1 | 2.45.3 | `unplugin-vue-components` + `NaiveUiResolver` 自动注册模板中的 `n-*`;在 `h()` 渲染函数里用 `NButton` 等须显式 `import { NButton } from 'naive-ui'` |
| vue-router | ^5.1.0 | 5.3.1 | `createWebHistory/createWebHashHistory` |
| pinia / pinia-plugin-persistedstate | ^3.0.4 / ^4.7.1 | 3.0.4 / 4.7.1 | |
| unocss | ^66.7.0 | 66.10.0 | `presetWind3 + presetAttributify + presetIcons + presetRemToPx(baseFontSize 4)` → `p-12` = 12px |
| echarts / vue-echarts | ^6.1.0 / ^8.0.1 | 6.1.0 / 8.3.0 | 仅 `views/home/index.vue` 使用 |
| axios | ^1.16.1 | 1.20.0 | |
| @arco-design/color | ^0.4.0 | 0.4.0 | 主题色梯度 |
| @vueuse/core | ^14.2.1 | — | `useDark/useStorage/useFullscreen/useClipboard/hyphenate` |
| dayjs / lodash-es | — | — | `formatDateTime`、`cloneDeep` |
| xlsx | ^0.18.5 | — | 仅 `MeCrud.handleExport`;独占 384 KB chunk |
| vue3-intro-step(dev) | ^1.0.5 | — | 仅 `BeginnerGuide.vue`;`vite.config.js optimizeDeps.include` 引用 |
| @microsoft/signalr / @microsoft/signalr-protocol-msgpack | 无 | — | 需新增;`npm view` 已确认 10.0.11 存在,msgpack 包依赖 `@microsoft/signalr >=10.0.11`、`@msgpack/msgpack ^2.7.0` |

其余 devDeps:`@antfu/eslint-config`、`unplugin-auto-import`(`imports: ['vue','vue-router']`,因此所有 `.js/.vue` 里 `ref/computed/watch/h/nextTick/markRaw/defineAsyncComponent/useRoute/useRouter` 等**无需 import**;eslint 的 `globals` 同步声明了 `$message/$dialog/$notification/$loadingBar`)、`vite-plugin-router-warn`(压掉 "No match found" 警告)、`vite-plugin-vue-devtools`、`rollup-plugin-visualizer`(未用)、`simple-git-hooks`、`lint-staged`、`taze`、`esno`、`@iconify/json`(全量 iconify 数据,供静态 `i-xxx:yyy` class)。

### 1.2 工程配置文件

| 文件 | 关键内容 | 对我们的影响 |
|---|---|---|
| `.npmrc` | `registry=https://registry.npmmirror.com`、`shamefully-hoist=true`、`strict-peer-dependencies=false` | 镜像可用(22 s 完成);后两项是 pnpm 项,npm 忽略 |
| `.env` | `VITE_TITLE = 'Vue Naive Admin'` | 用于 `index.html` 的 `%VITE_TITLE%`、登录页标题、侧栏标题、`document.title` 后缀 |
| `.env.development` | `VITE_USE_HASH='true'`、`VITE_PUBLIC_PATH='/'`、`VITE_AXIOS_BASE_URL='https://m1.apifoxmock.com/m1/3776410-3408296-default'`、`VITE_PROXY_TARGET='http://localhost:8085'` | 开发默认 **hash 路由 + 直连云 mock**(不走代理) |
| `.env.production` | `VITE_USE_HASH='false'`、`VITE_PUBLIC_PATH='/'`、`VITE_AXIOS_BASE_URL='https://m1.apifoxmock.com/...'` | 生产默认 history 路由;mock 地址被打进产物(实测 `store-*.js` 内含该 URL) |
| `vite.config.js` | `base: VITE_PUBLIC_PATH \|\| '/'`;插件 Vue / VueJsx / VueDevTools / Unocss / AutoImport / Components(NaiveUiResolver)/ `pluginPagePathes` / `pluginIcons` / `removeNoMatch`;alias `@`→`src`、`~`→根;`server: { host:'0.0.0.0', port:3200, open:false, proxy: { '/api': { target: VITE_PROXY_TARGET, changeOrigin, rewrite: 去掉 ^/api, secure:false, proxyRes 加 x-real-url 头 }, '/runapi': {...} } }`;`build.chunkSizeWarningLimit: 1024` | **`/api` 代理会剥掉 `/api` 前缀**,与 Master 的 `/api/...` 路由冲突,必须删 rewrite;无 `/hubs` ws 代理;无 `build.outDir` |
| `uno.config.js` | 图标集合 `me`→`src/assets/icons/isme/*.svg`、`fe`→`src/assets/icons/feather/*.svg`;`safelist` = `dynamic-icons.js` + 全部 `i-fe:*`/`i-me:*`(含 `?mask` 变体);shortcuts `wh-full / f-c-c / flex-col / card-border / auto-bg / auto-bg-hover / text-highlight`;rule `card-shadow`;theme color `primary: 'rgba(var(--primary-color))'`、`dark:'#18181c'`、`light_border/dark_border` | **菜单图标是运行时动态 class,只有 safelist 里的能渲染**:后端菜单树里的 `icon` 必须是 `i-fe:<feather 名>` / `i-me:<isme 名>`,或提前加进 `src/assets/icons/dynamic-icons.js`(模板示例 `i-simple-icons:juejin`) |
| `build/index.js` | `getIcons()`(生成 safelist)、`getPagePathes()`(glob `src/views/**/*.vue` → `/src/views/...`) | 后者产物 `isme:page-pathes` 虚拟模块仅供 pms 资源管理页下拉选组件路径 |
| `build/plugin-isme/*` | 虚拟模块 `isme:icons`、`isme:page-pathes` | 无害,可保留 |
| `jsconfig.json` | 路径别名给编辑器 | 保留 |
| `index.html` | `<html lang="en">`;`<link rel="icon" href="/favicon.png">`;`<title>%VITE_TITLE%</title>`;内联 loading 骨架;`<body class="dark:text-#e9e9e9 auto-bg">`;`<script type="module" src="/src/main.js">` | Vite 会按 `base` 重写 icon/script 路径(实测 `/admin/favicon.png`) |
| `public/` | 仅 `favicon.png` | 原样复制到 dist 根 |
| `.gitignore` | `*.local node_modules dist stats.html` | |
| `pnpm-lock.yaml`、`pnpm-workspace.yaml` | pnpm 专用 | 本仓库用 npm(已有 `package-lock.json`),删除 |
| `eslint.config.js` | antfu 配置 + unocss | 非门禁 |

### 1.3 本机构建实证(2026-09-08)

| 项 | 结果 |
|---|---|
| Node / npm | v22.22.2 / 10.9.7 |
| `npm install --no-audit --no-fund` | 22 s(依赖已在 09-07 装过一次,本次 "up to date");501 个顶层包,`node_modules` 903 MB;`postinstall` 执行 `npx simple-git-hooks` 并输出 "Successfully set the pre-commit with command: npx lint-staged" —— **写入了 `C:/Users/ming/claude/server-node-monitor/.git/hooks/pre-commit`**(`git rev-parse --show-toplevel` 证实仓库根是 git 仓库;hook 内容为 `npx lint-staged`,而根目录没有 package.json) |
| `npm run build`(默认 `.env.production`) | 17 s 挂钟(Vite 报 `built in 10.51s`,其中 unocss `renderChunk` 1.9 s);EXIT 0;`✓ 3458 modules transformed` |
| 警告 | 仅 1 条:`Your Vite config uses features that are unsupported by configLoader: 'native'`(`import './build/plugin-isme'` 目录索引、`./icons`/`./page-pathes`/`..` 无扩展名等 5 处);不影响构建,后续修 vite.config 时补 `/index.js` 与 `.js` 扩展名即可消除 |
| dist 体积 | 97 个文件 2 478 157 B;js 87 个、css 4 个、其它 6 个(favicon、login 背景 webp 等);js+css gzip 合计 ≈ 740 KB。最大 chunk:`home-*.js` 595 KB(ECharts)、`crud-*.js` 384 KB(xlsx)、`index-*.css` 270 KB、`store-*.js` 219 KB(naive-ui 公共 + pinia + axios)、`upload-*.js` 79 KB、`translate-*.js` 33 KB |
| `dist/index.html` | 34 个 `<link rel="modulepreload" crossorigin>` + 1 个 `<script type="module" crossorigin src="/assets/index-*.js">` + 1 个 stylesheet;资源路径以 `/assets/` 开头(base `/`) |
| 产物里被打进的环境值 | axios `baseURL:` = Apifox URL;登录页 captcha URL 同;`import.meta.glob` key 列表 = 20 个 `/src/views/...vue` 绝对路径(见 §5.3) |
| **`/admin/` 基址构建**(`MSYS_NO_PATHCONV=1 VITE_PUBLIC_PATH=/admin/ VITE_USE_HASH=false VITE_AXIOS_BASE_URL=/api VITE_TITLE='Server Node Monitor' npx vite build --outDir dist-admin`) | 14 s;`index.html` 变为 `<link rel="icon" href="/admin/favicon.png">`、`<script src="/admin/assets/index-*.js">`、`<link href="/admin/assets/index-*.css">`、`<title>Server Node Monitor`;JS 内含 ``createWebHistory(`/admin/`)`` 与动态 import 前缀 `` `/admin/`+e ``;axios ``baseURL:`/api` ``。证明**只改 env 即可挂到 `/admin/`** |
| MSYS 坑 | 不加 `MSYS_NO_PATHCONV=1` 时,`VITE_PUBLIC_PATH=/admin/` 被 Git Bash 转成 `/Program Files/Git/admin/`,产物路径全错。`.env` 文件中的值不受影响 |
| 清理 | `dist/`、`dist-admin/` 已删除;`node_modules` 保留(git-ignored);未改任何模板源文件 |

---

## 2. 启动与运行时流程(源码事实)

```
src/main.js  bootstrap():
  createApp(App) → setupStore(pinia + persistedstate) → setupDirectives(v-permission)
  → await setupRouter(app)  (createRouter + 4 个守卫) → app.mount('#app')
  → setupNaiveDiscreteApi()  (window.$message/$dialog/$notification/$loadingBar,跟随 isDark/themeOverrides)
```

`App.vue`:`<n-config-provider :locale="zhCN" :date-locale="dateZhCN" :theme="isDark ? darkTheme : undefined" :theme-overrides="appStore.naiveThemeOverrides">` 包住 `<router-view>`;布局组件 = `getLayout(route.meta.layout || appStore.layout)`,即 `markRaw(defineAsyncComponent(() => import(`@/layouts/${name}/index.vue`)))`(Map 缓存防闪烁);`<KeepAlive :include="tabStore.tabs.filter(keepAlive).map(name)">`,页面组件 `:key="curRoute.fullPath"`,`tabStore.reloading` 为 true 时卸载以实现"重新加载";`watchEffect(() => appStore.setThemeColor(primaryColor, isDark))`;`layoutSettingVisible` 控制右侧悬浮"布局设置"按钮。

路由守卫顺序(`src/router/guards/index.js`):`page-loading`(`$loadingBar.start/finish/error`)→ **`permission-guard`** → `page-title`(`${meta.title} | VITE_TITLE`)→ `tab-guard`(排除 `/404 /403 /login`,`addTab({ name, path: fullPath, title, icon, keepAlive })`)。

**permission-guard 精确逻辑**(`src/router/guards/permission-guard.js`):

```
WHITE_LIST = ['/login', '/404']
无 token:
  to.path ∈ WHITE_LIST → 放行;否则 → return { path: 'login', query: { ...to.query, redirect: to.path } }
                                           ↑ 相对路径!仅因初次导航 current 为 '/' 才恰好解析为 '/login'(见 §12.4 修正)
有 token:
  to.path === '/login' → { path: '/' }
  to.path ∈ WHITE_LIST → 放行
  userStore.userInfo 为空(首次进入 / 刷新页面):
      [user, permissions] = await Promise.all([getUserInfo(), getPermissions()])   // 两个请求并发
      userStore.setUser(user); permissionStore.setPermissions(permissions)
      routeComponents = import.meta.glob('@/views/**/*.vue')
      accessRoutes.forEach(r => { r.component = routeComponents[r.component] || undefined
                                  !router.hasRoute(r.name) && router.addRoute(r) })
      return { ...to, replace: true }        // 用新路由表重新导航一次
  router.getRoutes().some(r => r.name === to.name) → 放行
  否则 { data: hasMenu } = await api.validateMenuPath(to.path)
      hasMenu ? { name:'403', query:{ path: to.fullPath }, state:{ from:'permission-guard' } }
              : { name:'404', query:{ path: to.fullPath } }
```

要点:(1) `getPermissions()` 内部 try/catch,接口失败只 `console.error`,仍返回 `basePermissions`(`settings.js` 的外链菜单)——所以菜单接口挂了页面也能进,只是没菜单;`getUserInfo()` 失败则守卫抛错、导航中止(白屏 + loadingBar error)。(2) `userInfo/permissions` 不持久化(只有 `auth.accessToken`、`app`、`tab` 持久化),每次刷新页面都重新拉这两个接口。(3) `router.hasRoute(name)` 判重 → 树里 `code:'Home'` 与 `basic-routes.js` 预注册的 `Home`(`path:'/'`, `meta.title:'首页'`)同名时**不会重复添加**,因此首页的 `meta.title/keepAlive` 以 `basic-routes.js` 为准,侧栏 label 以树为准。

`basic-routes.js` 预注册:`Login /login`(layout empty)、`Home /`、`404 /404`(layout empty)、`403 /403`(layout empty)。**没有 `/:pathMatch(.*)*` 通配**,未匹配路径由守卫用 `validateMenuPath` 区分 403/404。

`auth.logout()` = `resetLoginState()`(`resetRouter(accessRoutes)` 逐个 `removeRoute` → `resetUser` → `resetPermission` → `resetTabs` → `resetToken`)+ `router.replace({ path: '/login', query: route.query })`。

---

## 3. HTTP 层精确行为(`src/utils/http/*`)

```js
// index.js
createAxios({ baseURL: import.meta.env.VITE_AXIOS_BASE_URL, timeout: 12000 })  → export const request
createAxios({ baseURL: '/mock-api' })                                            → export const mockRequest(未用)
```

请求拦截 `reqResolve`:`config.needToken === false` 则不加头;否则若 `useAuthStore().accessToken` 存在 → `config.headers.Authorization = 'Bearer ' + accessToken`。**没有任何自定义头**,没有 CSRF。自定义 config 字段:`needToken`(默认 true)、`needTip`(默认 true;axios 1.x 会透传未知字段)。

响应拦截 `resResolve`(HTTP 2xx):

| 条件 | 行为 |
|---|---|
| `content-type` 含 `json` 且 `data.code ∈ [0, 200]` | `resolve(data)` → 调用方拿到整个信封 `{code,message,data}`(模板所有 api 调用都写成 `const { data } = await api.xxx()`) |
| `content-type` 含 `json` 且 code 其它 | `code = data.code ?? status`;`message = resolveResError(code, data.message ?? statusText, needTip)`;`reject({ code, message, error: data })` |
| 非 JSON(文本 / 二进制 / **204 空体**) | `resolve(data ?? response)` —— 204 时 `data` 为空串,`??` 不触发,调用方 `const {data}` 得到空串。**Master 所有接口必须返回 200 + JSON 信封,不要用 204/空体** |

响应拦截 `resReject`(HTTP 非 2xx / 网络错误 / 超时):

| 条件 | 行为 |
|---|---|
| 无 `error.response`(断网、超时 `ECONNABORTED`、CORS) | `resolveResError(error.code, error.message)` → 默认分支显示 axios 的英文 message(如 `timeout of 12000ms exceeded`)→ `$message.error`;reject |
| 有 response | `code = data?.code ?? status`;`resolveResError(code, data?.message ?? error.message, needTip)`;`reject({ code, message, error: response.data \|\| response })` |

`resolveResError(code, message, needTip = true)`(`helpers.js`):

| code | 行为 |
|---|---|
| `401` | `handleAuthExpired('登录已过期,是否重新登录?', needTip)`:模块级 `isConfirming` 防重复弹;`$dialog.confirm({ type:'info' })`,确定 → `useAuthStore().logout()` + "已退出登录";取消 → 什么都不做(页面停留,后续请求继续 401)。返回 `false`(不再弹 message) |
| `11007` / `11008` | 同上,文案 `${服务端 message},是否重新登录?` |
| `403` | 文案固定 `请求被拒绝`(**覆盖**服务端 message) |
| `404` | 固定 `请求资源或接口不存在` |
| `500` | 固定 `服务器发生异常` |
| 其它(含 400/409/422/429/1xxxx 业务码/网络错误码) | 使用传入 `message`,为空则 `【code】: 未知异常!` |

末尾 `needTip && window.$message?.error(message)`。因此:**服务端返回 `{code: <非0>, message: "<中文>"}` 时,前端会原样弹出 message**;`needTip:false` 的调用(模板里只有 `logout`)静默。

**结论(Master 契约)**:
- 成功:HTTP 200,`Content-Type: application/json; charset=utf-8`,`{"code":0,"message":"OK","data":<any>}`(`data` 可为对象/数组/布尔/null,但 `code` 不可省)。
- 失败:HTTP 状态可为 4xx/5xx,body 仍为信封 `{"code":<int>,"message":"<中文>","data":null}`;拦截器优先取 body 的 `code`。
- 未登录/令牌失效:HTTP 401 + `code:401`(或 `11007/11008` 自定义文案)→ 前端弹重新登录对话框。
- 不要返回 204;不要让默认 `ProblemDetails`(`application/problem+json`,`content-type` 含 `json`)出去:拦截器会走信封分支,`data.code` 不存在 → `code = status`,`message = data.message ?? statusText` 为 undefined → 显示 `【400】: 未知异常!`。因此**必须用自定义信封替换默认 ProblemDetails,包括模型绑定失败(400)与未处理异常(500)**。

---

## 4. 模板调用的全部后端端点(方法 / 路径 / 请求 / 响应 / mock 实测)

路径均相对 `VITE_AXIOS_BASE_URL`(我们改为 `/api`)。除标注 `needToken:false` 外都带 `Authorization: Bearer <accessToken>`。

### 4.1 核心(必须兼容)

| # | 调用位置 | 方法 & 路径 | 请求 | 响应(mock 实测) |
|---|---|---|---|---|
| 1 | `views/login/api.js login` | `POST /auth/login`,`needToken:false` | `{"username":"admin","password":"123456","captcha":"","isQuick":true}`(登录页把 `captcha`(可空)和 `isQuick`(一键体验时 true)一起发;`password.toString()`) | `{"code":0,"message":"OK","data":{"accessToken":"access-token:admin:super-admin"}}`;前端 `authStore.setToken(data)` 只读 `data.accessToken`;然后 `router.push(route.query.redirect ?? '/')`。失败时 `error.code === 10003` 视为"验证码错误"并刷新验证码 |
| 2 | `store/helper.js getUserInfo` ← `api.getUser` | `GET /user/detail` | — | 见 §6(mock 实测完整 JSON) |
| 3 | `store/helper.js getPermissions` ← `api.getRolePermissions` | `GET /role/permissions/tree` | — | `{"code":0,"message":"OK","data":[<菜单树>]}`,见 §5.1 |
| 4 | `permission-guard` ← `api.validateMenuPath` | `GET /permission/menu/validate?path=<to.path>`(path 直接拼进 query,未 encode) | — | mock **404 不存在**(源码推断:`{"code":0,"data":true\|false}`,守卫解构 `const { data: hasMenu }`) |
| 5 | `layouts/components/UserAvatar.vue`、`RoleSelect.vue` ← `api.logout` | `POST /auth/logout`,body `{}`,`needTip:false` | `{}` | `{"code":0,"message":"OK","data":true}`;前端忽略返回值(try/catch),随后本地 `authStore.logout()` |

### 4.2 个人中心(`views/profile`,建议保留页面)

| # | 调用 | 方法 & 路径 | 请求 | 响应 |
|---|---|---|---|---|
| 6 | `profile/api.js changePassword` | `POST /auth/password` | `{"oldPassword":"...","newPassword":"..."}` | mock 404;源码推断成功返回 `{"code":0,"data":...}` 即可;成功后前端 `$message.success('密码修改成功')` 并重新 `getUserInfo()`(**不会**自动登出) |
| 7 | `profile/api.js updateProfile` | `PATCH /user/profile/{id}`(`{id}` = `userStore.userId`) | `{"id":1,"nickName":"...","gender":0\|1\|2,"address":"...","email":"..."}` 或 `{"id":1,"avatar":"<url>"}` | mock 404;成功后重新拉 `/user/detail` |

### 4.3 仅 pms/demo 页面调用(随页面删除,Master 不实现,返回 404 信封即可)

| 调用文件 | 端点 | mock 实测 |
|---|---|---|
| `api/index.js switchCurrentRole`(`RoleSelect.vue`,仅 `roles.length > 1` 时可见) | `POST /auth/current-role/switch/{roleCode}` | `{"code":0,"message":"OK","data":{"accessToken":"access-token:admin:super-admin"}}`;前端 `authStore.switchCurrentRole(data)` 后 `location.reload()` |
| `api/index.js refreshToken`(**无调用者**) | `GET /auth/refresh/token` | 404 |
| `login/api.js toggleRole`(无调用者) | `POST /auth/role/toggle` | 404 |
| `login/index.vue initCaptcha`(`<img :src>`) | `GET /auth/captcha?<ts>` | `image/svg+xml` 验证码图 |
| `pms/resource/api.js` | `GET /permission/menu/tree`、`GET /permission/button/{parentId}`、`POST /permission`、`PATCH /permission/{id}`、`DELETE permission/{id}`(缺前导 `/`)、`GET ${VITE_PUBLIC_PATH}components.json`(死代码,`public/` 无此文件) | tree 同 §5.1 结构(全量);button → `{"code":0,"data":[]}`;写操作 → `400 {"code":30001,"error":"CustomException","message":"预览环境不支持此操作"}` |
| `pms/role/api.js` | `POST /role`、`GET /role/page?pageNo&pageSize&name&enable`、`PATCH /role/{id}`、`DELETE /role/{id}`、`GET /permission/tree`、`GET /user?pageNo&pageSize&...`、`PATCH /role/users/add/{roleId}` `{userIds}`、`PATCH /role/users/remove/{roleId}` | `/role/page` → `{"code":0,"data":{"pageData":[{"id":1,"code":"SUPER_ADMIN","name":"超级管理员","enable":true,"permissionIds":[]},{"id":2,"code":"ROLE_QA",...}],"total":2}}` |
| `pms/user/api.js` | `POST /user`、`GET /user`、`PATCH /user/{id}`、`DELETE /user/{id}`、`PATCH /user/password/reset/{id}`、`GET /role?enable=1` | `/user` → `{"code":0,"data":{"pageData":[{id,username,enable,createTime,updateTime,roles:[...],gender,avatar,address,email}],"total":1}}`;`/role?enable=1` → `data:[{id,code,name,enable}]` |

### 4.4 分页约定(`components/me/crud/index.vue` `MeCrud`,新页面若复用必须遵守)

- 入参:`getData({ ...queryItems, pageNo, pageSize })`(`remote && isPagination` 时;默认 `pageSize=10`,`page=1`)。
- 出参:`const { data } = await getData(params)`;`tableData = data.pageData || data`;`itemCount = data.total ?? data.length`。即分页接口返 `data:{pageData:[...],total:N}`,非分页返 `data:[...]`。
- 当前页为空且 `total>0` 且 `page>1` 时自动回退上一页。`rowKey` 默认 `id`。`handleReset` 把 queryItems 全部置 `null` 再合并初始值。

---

## 5. 权限/菜单树:JSON 模式与路由生成

### 5.1 节点字段(mock 实测,`GET /role/permissions/tree` 节选)

```json
{
  "id": 4, "name": "用户管理", "code": "UserMgt", "type": "MENU", "parentId": 2,
  "path": "/pms/user", "redirect": null, "icon": "i-fe:user",
  "component": "/src/views/pms/user/index.vue", "layout": null, "keepAlive": true,
  "method": null, "description": null, "show": true, "enable": true, "order": 3,
  "children": [
    { "id": 13, "name": "创建新用户", "code": "AddUser", "type": "BUTTON", "parentId": 4,
      "path": null, "redirect": null, "icon": null, "component": null, "layout": null,
      "keepAlive": null, "method": null, "description": null, "show": true, "enable": true, "order": 1 }
  ]
}
```

mock 里另有隐藏详情页范例:`{"code":"RoleUser","path":"/pms/role/user/:roleId","component":"/src/views/pms/role/role-user.vue","layout":"full","show":false,"enable":true}` 作为 `RoleMgt` 的子节点;分组父项范例:`{"code":"SysMgt","path":null,"component":null,"children":[...]}`。

| 字段 | 类型 | 前端读取位置与语义 |
|---|---|---|
| `id` | int | 仅 pms 资源管理页与 `BreadCrumb` 的 key;新方案中随意但唯一 |
| `code` | string | **路由 `name`**、菜单 `key`、KeepAlive 组件名、`BreadCrumb` 匹配键(`route.name`)、外链 iframe 路径 `hyphenate(code)`;大驼峰;全树唯一;与 `basic-routes` 同名(`Home/Login/403/404`)不会重复注册 |
| `name` | string | 菜单 label、`meta.title`(标签页、`document.title`、`CommonPage` 默认标题) |
| `type` | `'MENU'` \| `'BUTTON'` | 只有 MENU 参与菜单/路由;BUTTON 只进父路由 `meta.btns`(`v-permission` 用) |
| `parentId` | int\|null | 菜单生成**不读**(靠 `children` 嵌套);仅 pms 页面用 |
| `path` | string\|null | 路由 path(可含 `:param`);分组父项可 `null/""`(不注册路由,只渲染子菜单);`http(s)://`/`mailto:`/`tel:` 开头 → 外链:`originPath=path`,`component='/src/views/iframe/index.vue'`,`path='/iframe/<hyphenate(code)>'`,点击时弹"外链打开 / 在本站内嵌打开" |
| `redirect` | string\|null | 直接透传给路由 `redirect` |
| `icon` | string\|null | 菜单图标 class,渲染为 `h('i', { class: `${icon} text-16` })`;`meta.icon = icon + '?mask'`(标签栏/面包屑用);**必须在 unocss safelist 内**(`i-fe:*`、`i-me:*` 或 `dynamic-icons.js` 列出的) |
| `component` | string\|null | `import.meta.glob('@/views/**/*.vue')` 的 key,实测 key 形式为 `/src/views/<dir>/<file>.vue`;找不到 → `component: undefined` → 该路由渲染空白(不报错) |
| `layout` | `''`\|`null`\|`'normal'`\|`'simple'`\|`'full'`\|`'empty'` | `meta.layout`;空 → 跟随 `appStore.layout` |
| `keepAlive` | bool\|null | `meta.keepAlive = !!keepAlive`;**生效条件**:组件 `defineOptions({ name: '<code>' })`,因为 `KeepAlive include` 用 tab 的 `name`(= route.name = code) |
| `show` | bool | `false`:不进侧栏,但只要 `enable && path` 仍注册路由,且 `meta.parentKey = 父菜单 key` → 侧栏高亮父项 |
| `enable` | bool | `false`:不注册路由(若 `show:true` 侧栏仍显示,点击后走 403/404 判定) |
| `order` | int | 同级升序(缺省 0) |
| `children` | array | 递归;MENU 子项进菜单,BUTTON 子项进 `meta.btns` |
| `method` / `description` | — | 前端不读,可省略 |

### 5.2 生成算法(`store/modules/permission.js`,逐行等价描述)

```
setPermissions(permissions):
  this.permissions = permissions
  this.menus = permissions.filter(type==='MENU').map(getMenuItem).filter(Boolean).sort(order)

getMenuItem(item, parent):
  route = generateRoute(item, item.show ? null : parent?.key)
  if (item.enable && route.path && !route.path.startsWith('http')) accessRoutes.push(route)
  menuItem = { label: route.meta.title, key: route.name, path: route.path, originPath: route.meta.originPath,
               icon: () => h('i', { class: `${route.meta.icon} text-16` }), order: item.order ?? 0 }
  children = item.children.filter(MENU) → 递归 getMenuItem(child, menuItem) → 过滤空 → 排序;为空则删 children 键
  return item.show ? menuItem : null          // 隐藏项的子项仍会被递归并注册路由

generateRoute(item, parentKey):
  外链处理(见 §5.1 path 行)
  return { name: item.code, path: item.path, redirect: item.redirect, component: item.component,
           meta: { originPath, icon: `${item.icon}?mask`, title: item.name, layout: item.layout,
                   keepAlive: !!item.keepAlive, parentKey,
                   btns: item.children?.filter(BUTTON).map(b => ({ code: b.code, name: b.name })) } }
```

`getPermissions()` 返回 `cloneDeep(basePermissions).concat(asyncPermissions)` —— `settings.js` 的 `basePermissions`(外链菜单"项目文档/接口文档/Naive UI/博客-掘金",order 98)**排在前面**,必须清空。

侧栏 `SideMenu.vue`:`<n-menu accordion :indent="18" :collapsed-width="64" :options="permissionStore.menus" :value="route.meta.parentKey || route.name">`;点击 `router.push(item.path)`(无 path 的分组项不跳);路由变化后 `menu.showOption()` 自动展开。`BreadCrumb.vue`(full 布局)在 `permissions` 树里按 `code === route.name` 找祖先链,面包屑可下拉跳同级(只显示 `show:true` 的)。`UserAvatar.vue` 的"个人资料"项 `show: accessRoutes.some(r => r.path === '/profile')`,"切换角色"项 `show: roles.length > 1`。

### 5.3 `component` 与 `src/views` 的映射(构建实测 key 全集)

默认构建中 `import.meta.glob` 打进产物的 20 个 key:`/src/views/base/{index,keep-alive,test-modal,unocss-icon,unocss}.vue`、`/src/views/demo/{translate,upload}/index.vue`、`/src/views/error-page/{403,404}.vue`、`/src/views/home/index.vue`、`/src/views/iframe/index.vue`、`/src/views/login/index.vue`、`/src/views/pms/resource/{index,components/MenuTree,components/QuestionLabel,components/ResAddOrEdit}.vue`、`/src/views/pms/role/{index,role-user}.vue`、`/src/views/pms/user/index.vue`、`/src/views/profile/index.vue`。规则:**任意深度、任意文件名**的 `.vue` 都会成为可选组件(包括 `components/` 子目录),key = 以 `/src/` 开头的绝对路径。新页面放在 `src/views/<模块>/index.vue`(或 `detail.vue`),菜单树写 `"/src/views/<模块>/index.vue"`。

### 5.4 按钮级权限

`v-permission="'AddUser'"`(`directives/index.js`):`mounted` 时读 `router.currentRoute.meta.btns[].code`,不含则 `el.remove()`;`withPermission(vnode, code)` 供 `h()` 渲染函数使用(模板 `pms/user` 用它渲染"超管专属"按钮)。**单管理员场景不需要**;若菜单树不带 BUTTON,`meta.btns` 为 `undefined`,`v-permission` 会移除元素——新页面不要用 `v-permission`,或树里补齐 BUTTON。

---

## 6. 用户信息形状(mock 实测 `GET /user/detail`)

```json
{"code":0,"message":"OK","data":{
  "id":1,"username":"admin","enable":true,
  "createTime":"2023-11-18T08:18:59.150Z","updateTime":"2023-11-18T08:18:59.150Z",
  "profile":{"id":1,"nickName":"Admin","gender":null,
             "avatar":"https://wpimg.wallstcn.com/f778738c-e4f8-4870-b634-56703b4acafe.gif?imageView2/1/w/80/h/80",
             "address":null,"email":null,"userId":1},
  "roles":[{"id":1,"code":"SUPER_ADMIN","name":"超级管理员","enable":true},
           {"id":2,"code":"ROLE_QA","name":"质检员","enable":true}],
  "currentRole":{"id":1,"code":"SUPER_ADMIN","name":"超级管理员","enable":true}}}
```

`store/helper.getUserInfo()` 映射为 `userStore.userInfo = { id, username, avatar: profile?.avatar, nickName: profile?.nickName, gender: profile?.gender, address: profile?.address, email: profile?.email, roles, currentRole }`。UI 使用:`UserAvatar`(`n-avatar :src="avatar"`、`nickName ?? username`、`[currentRole.name]`)、首页问候、`profile` 页(nickName / gender(0 保密 1 男 2 女)/ address / email)、`RoleSelect`(roles)。`enable/createTime/updateTime` 不读。`roles.length <= 1` 时"切换角色"自动隐藏,所以单角色返回一个 `SUPER_ADMIN` 即可。`avatar` 为空时 `n-avatar` 显示空圆;建议服务端返回本地默认头像 URL(`/admin/avatar.svg`,放在 `web/admin/public/avatar.svg`)。

---

## 7. 新增一个页面 + 菜单项(步骤)

1. 新建 `src/views/<module>/index.vue`;若菜单 `keepAlive:true`,组件内 `defineOptions({ name: '<Code>' })`(与树 `code` 一致);页面外壳用 `<CommonPage>`(顶部标题栏 + 圆角卡片;`title` 默认 `route.meta.title`;插槽 `#action`、`#title-prefix`、`#title-suffix`、`#header`、`#footer`;属性 `back`、`show-header`、`show-footer`)或 `<AppPage>`(纯滚动容器;`full`、`show-footer`)。
2. 数据访问按模板惯例放 `src/views/<module>/api.js`:`import { request } from '@/utils'; export default { list: params => request.get('/nodes', { params }), create: data => request.post('/nodes', data), ... }`。
3. 在 Master 的静态菜单树里加一条 `{"code":"<Code>","name":"<中文>","type":"MENU","path":"/<path>","component":"/src/views/<module>/index.vue","icon":"i-fe:<feather 图标名>","layout":"","keepAlive":false,"show":true,"enable":true,"order":N,"children":[]}`;详情页等隐藏页设 `show:false` 并作为父菜单的 `children`。
4. 图标只能用 `src/assets/icons/feather/*.svg` 的文件名(如 `i-fe:home / server / activity / bell / settings / user / cpu / hard-drive / globe / bar-chart-2 / list / alert-circle / send / wifi / clock / dollar-sign / map-pin / key / shield / terminal / download`)或 `isme/*.svg`;要用 iconify 其他集合须写入 `src/assets/icons/dynamic-icons.js`。写死在 `.vue` 模板里的 `i-material-symbols:*`、`i-carbon:*`、`i-mdi:*` 不受限(UnoCSS 扫源码,数据来自 `@iconify/json`)。
5. 表格页用 `<MeCrud ref="$table" :columns :get-data="api.list" v-model:query-items="queryItems" :scroll-x>` + `<MeQueryItem label>`,`onMounted(() => $table.value.handleSearch())`;弹窗用 `<MeModal ref>` + `useModal()` / `useForm()` / `useCrud({ name, initForm, doCreate, doUpdate, doDelete, refresh })`。
6. 无需改 `router/*`:路由由菜单树动态注册;刷新页面自动重建。

---

## 8. 文件保留/删除清单(带依赖说明)

**删除**:
- `src/views/pms/**`(资源/角色/用户管理,依赖多角色 RBAC 后端)。
- `src/views/demo/**`(上传演示;`translate/` 内置第三方 API key 与 `/runapi` 代理,必须删)。
- `src/views/base/**`(组件/unocss/keep-alive/modal 演示)。
- `src/views/iframe/index.vue`(外链内嵌;`permission.js generateRoute` 的外链分支保留也无害,只要树里没有 http 路径)。
- `src/layouts/components/RoleSelect.vue`(被 `UserAvatar.vue` 引用,需同步删引用与 `layouts/components/index.js` 导出)、`BeginnerGuide.vue`(依赖 `vue3-intro-step`;被 `normal/header`、`full/header` 引用,需同步删 + 删 `vite.config.js optimizeDeps.include`)。
- `src/assets/icons/isme/{apifox,gitee,docs,naiveui,awesome}.svg`(品牌图标;`dialog.svg` 可留;目录本身保留,`FileSystemIconLoader` 对空目录不报错)、`src/assets/images/{isme.png,login_banner.webp,login_bg.webp}`(替换为项目素材后删;`TheLogo.vue`、登录页有引用)。
- `pnpm-lock.yaml`、`pnpm-workspace.yaml`;`.vscode/` 可选。
- `package.json`:`scripts.postinstall`、`scripts.up`、`simple-git-hooks`、`lint-staged` 字段;devDeps `simple-git-hooks lint-staged taze esno vue3-intro-step rollup-plugin-visualizer`;deps `xlsx`(同时删 `MeCrud` 的 `import { utils, writeFile } from 'xlsx'`、`handleExport` 与 `defineExpose` 中的引用,省 384 KB)。

**保留(核心骨架)**:`build/**`、`src/main.js`、`App.vue`、`settings.js`(改内容)、`api/index.js`(改)、`utils/**`(改 http)、`store/**`(改 auth)、`router/**`(改 basic-routes、guard 小修)、`layouts/**`(删引用)、`components/common/**`、`components/me/**`、`composables/**`、`directives/**`、`views/login`(改)、`views/home`(重写为总览)、`views/profile`(改)、`views/error-page`、`styles/**`、`assets/icons/feather/**`、`assets/icons/dynamic-icons.js`、`public/favicon.png`(替换)。

---

## 9. ECharts 用法(模板实况)

`src/views/home/index.vue`:
```js
import { BarChart, LineChart, PieChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import * as echarts from 'echarts/core'
import { UniversalTransition } from 'echarts/features'
import { CanvasRenderer } from 'echarts/renderers'
import VChart from 'vue-echarts'
echarts.use([TooltipComponent, GridComponent, LegendComponent, BarChart, LineChart, CanvasRenderer, UniversalTransition, PieChart])
// <div class="h-400"><VChart :option="trendOption" autoresize /></div>
```
- 按需注册(`echarts/core` + `use`),没有全局 `app.component('v-chart')`,`VChart` 是局部 import。
- 容器必须有确定高度(模板用 `h-400` = 400px),`autoresize` 跟随容器。
- 暗色:模板未处理。vue-echarts 8 支持 `:theme="appStore.isDark ? 'dark' : undefined"`;建议新建 `src/utils/echarts.js` 统一 `use([...])`(LineChart / BarChart、GridComponent / TooltipComponent / LegendComponent / DataZoomComponent / MarkLineComponent、CanvasRenderer、UniversalTransition)并 `export { default as VChart } from 'vue-echarts'`,页面只 import 该模块。
- ECharts 独占 ≈ 590 KB 未压缩,已被 Vite 按路由拆到 `home-*.js`;新页面多处引用会合并成公共 chunk,属正常。

---

## 10. 主题与布局

- **暗色**:`appStore.isDark = useDark()`(vueuse,`localStorage['vueuse-color-scheme']`,给 `<html>` 加 `dark` class);`ToggleTheme.vue` 用 `document.startViewTransition` 做圆形扩散动画(`global.css` 配套 `::view-transition-*` 规则);naive 侧 `<n-config-provider :theme="isDark ? darkTheme : undefined">`;UnoCSS 侧 `dark:` 变体;`setupNaiveDiscreteApi` 的 `configProviderProps` 也跟随。
- **主色**:`settings.js defaultPrimaryColor='#316C72'`、`naiveThemeOverrides.common.{primaryColor,primaryColorHover,primaryColorPressed,primaryColorSuppl}`;`appStore.setThemeColor(color, isDark)` 用 `@arco-design/color` 的 `generate(color, { list: true, dark: isDark })` 生成 10 级梯度,`colors[5]` 主色、`[4]` hover/suppl、`[6]` pressed,写回 `naiveThemeOverrides.common`,并 `document.body.style.setProperty('--primary-color', getRgbStr(colors[5]))`(形如 `49,108,114`),供 UnoCSS `text-primary/bg-primary/color-primary`(`rgba(var(--primary-color))`)与 `global.css` 滚动条、`SideMenu` 选中条使用。`ThemeSetting.vue` 提供 `n-color-picker`(swatches = arco `getPresetColors()`)。
- **布局**:`src/layouts/{normal,simple,full,empty}/index.vue`。`normal`:左侧 220/64px 侧栏(`SideLogo` + `SideMenu`)+ 60px 顶栏(`MenuCollapse` + **`AppTab` 标签栏** + `BeginnerGuide`/`ToggleTheme`/`Fullscreen`/GitHub/Gitee/`ThemeSetting`/`UserAvatar`);`full`:顶栏为 `BreadCrumb`,标签栏单独一行;`simple`:无顶栏,`UserAvatar` 与 `MenuCollapse` 在侧栏底部;`empty`:纯 `<slot>`(登录/403/404)。选择优先级:`route.meta.layout`(菜单树 `layout`)> `appStore.layout`(`defaultLayout='normal'`,`LayoutSetting` 悬浮按钮可改)。
- **标签栏**:`tabStore.tabs[{name,path(fullPath),title,icon,keepAlive}]`;右键菜单 重新加载/关闭/关闭其他/关闭左侧/关闭右侧;`reloadTab` 通过 `reloading` 开关卸载重挂。
- **持久化键**:`auth` → `localStorage['vue-naivue-admin_auth']`(模板拼写如此,内容 `{accessToken}`);`app` → `sessionStorage['app']`(`pick: ['collapsed','layout','primaryColor','naiveThemeOverrides']`);`tab` → `sessionStorage['tab']`(`pick: ['tabs']`);`lStorage/sStorage` 工具前缀 `vue-naive-admin_`(键小写化,值包 `{value,time,expire}`);登录页 `loginInfo` **明文保存密码**到 localStorage(必须改)。

---

## 11. 路由基址、history 模式与 `/admin/` 托管

### 11.1 模板接线

- `vite.config.js`:`base: VITE_PUBLIC_PATH || '/'` → 决定 `index.html` 资源前缀、动态 import 前缀、`public/` 文件 URL。
- `router/index.js`:`VITE_USE_HASH === 'true' ? createWebHashHistory(VITE_PUBLIC_PATH || '/') : createWebHistory(VITE_PUBLIC_PATH || '/')` → 决定浏览器地址形态。二者共用同一变量,**只能一起改**。
- 应用内所有跳转都是 router 相对路径(`/login`、`/`、`/profile`、`item.path`),history base 会自动补 `/admin`,**无需改任何 `router.push`**。登录成功 `router.push({ path: route.query.redirect, query })` 或 `'/'`;`redirect` 是 `to.path`(不含 base),同样无需改。
- 页面刷新:history 模式下浏览器请求 `/admin/nodes` 等深链,由服务端回退到 `admin/index.html`;`index.html` 引用的是绝对资源 `/admin/assets/*`,不受当前路径影响。

### 11.2 生产(Master 托管)

- 前端:`.env`(见 §12.5)`VITE_USE_HASH='false'`、`VITE_PUBLIC_PATH='/admin/'`、`VITE_AXIOS_BASE_URL='/api'`;`vite.config.js` 加 `build.outDir = path.resolve(process.cwd(), '../../src/SNM.Master/wwwroot/admin')`、`build.emptyOutDir = true`(根 `.gitignore` 已忽略 `src/SNM.Master/wwwroot/admin/`)。构建后校验:`wwwroot/admin/index.html` 中 `src="/admin/assets/index-*.js"`、`href="/admin/favicon.png"`(§1.3 已实测通过)。
- Master(顺序重要):
  ```csharp
  app.UseForwardedHeaders();
  app.UseDefaultFiles();            // "/" -> wwwroot/index.html(公开大屏)
  app.UseStaticFiles(new StaticFileOptions
  {
      OnPrepareResponse = ctx =>
      {
          var p = ctx.Context.Request.Path.Value ?? "";
          ctx.Context.Response.Headers.CacheControl =
              p.StartsWith("/admin/assets/", StringComparison.Ordinal)
                  ? "public, max-age=31536000, immutable"   // 文件名带内容 hash
                  : "no-cache";                              // index.html / favicon / avatar
      }
  });
  app.MapHub<AgentHub>("/hubs/agent"); app.MapHub<PublicHub>("/hubs/public"); app.MapHub<AdminHub>("/hubs/admin");
  var api = app.MapGroup("/api"); /* ... */
  api.MapFallback(() => Results.Json(new { code = 404, message = "接口不存在", data = (object?)null }, statusCode: 404)); // 未匹配 /api/* 返回信封,不落到 SPA
  app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");   // 仅 /admin 子树回退;/admin(无斜杠)也命中
  ```
  `nonfile` 约束避免 `/admin/assets/missing.js` 被回退成 HTML。`/admin` → `admin/index.html` → 前端 `createWebHistory('/admin/')` 正常工作(vue-router 会把 `/admin` 视作 base 去除后的 `/`)。
- 401 时 SPA 回退与 API 互不影响:`/api/*` 永远返回 JSON。

### 11.3 开发(Vite dev server + Master 5080)

`vite.config.js` `server.proxy` 精确写法(替换模板整段):
```js
server: {
  host: '0.0.0.0',
  port: 3200,
  open: false,
  proxy: {
    '/api':  { target: VITE_PROXY_TARGET, changeOrigin: true },              // 不再 rewrite:Master 路由本身就是 /api/...
    '/hubs': { target: VITE_PROXY_TARGET, changeOrigin: true, ws: true },    // SignalR:POST /hubs/admin/negotiate + WebSocket 升级
  },
},
```
`.env.development`:`VITE_PROXY_TARGET='http://127.0.0.1:5080'`,其余继承 `.env`(`VITE_PUBLIC_PATH='/admin/'`、`VITE_USE_HASH='false'`、`VITE_AXIOS_BASE_URL='/api'`)。开发地址 `http://localhost:3200/admin/`(Vite 按 base 服务;访问根路径会提示跳转)。同源代理 → **无需 CORS**(与 BRIEF §2.3 一致)。SignalR 浏览器端 `withUrl('/hubs/admin', { accessTokenFactory: () => useAuthStore().accessToken })` 用相对路径,同样经代理;`ws: true` 覆盖 negotiate(HTTP)与 WebSocket 升级两步。

### 11.4 脚本注意

`scripts/build-web.sh` 若用环境变量覆盖 Vite 值,必须 `export MSYS_NO_PATHCONV=1`(或 `MSYS2_ENV_CONV_EXCL='VITE_'`),否则 Git Bash 会把 `/admin/`、`/api` 转成 Windows 路径。推荐做法:值全部写进 `.env*`,脚本只跑 `npm ci --no-audit --no-fund && npm run build`。

---

## 12. 推荐改造方案(定案)

### 12.1 策略

**Master 兼容模板的登录/用户/菜单接口;前端做最小改造**(删多角色/验证码/演示,加 refresh 单飞,改 env/代理/品牌)。理由:守卫、动态路由、菜单、标签页、主题、`MeCrud/MeModal/CommonPage` 全部零改动复用;需要新写的只有拦截器 refresh 分支(约 40 行)与登录页减法。

### 12.2 Master 必须实现的模板兼容接口(精确形状)

通用:HTTP 200 + `application/json; charset=utf-8` + `{"code":0,"message":"OK","data":...}`;错误 `{"code":<int>,"message":"<中文>","data":null}`,HTTP 状态与语义一致(400 参数、401 认证、403、404、409、422、429、500);**禁用默认 ProblemDetails,不返回 204**。JSON camelCase,时间 ISO-8601 UTC。

| # | 方法 & 路径 | 鉴权 | 请求 | 成功响应 `data` | 失败 |
|---|---|---|---|---|---|
| 1 | `POST /api/auth/login` | 匿名,限速 | `{"username":"admin","password":"...","captcha":"","isQuick":false}`(后两项接受并忽略) | `{"accessToken":"<JWT>","refreshToken":"<43 字符随机>","tokenType":"Bearer","expiresIn":7200}` | `400 {"code":10001,"message":"用户名或密码错误"}`;锁定 `400 {"code":10002,"message":"失败次数过多,请稍后再试"}`;限速 `429 {"code":429,"message":"请求过于频繁,请稍后再试"}` |
| 2 | `POST /api/auth/refresh` **(新增)** | 匿名(凭 body) | `{"refreshToken":"..."}` | 同 #1(轮换:新 access + 新 refresh,旧 refresh 立即失效) | `401 {"code":11007,"message":"登录已过期"}`(refresh 无效/过期/已轮换) |
| 3 | `POST /api/auth/logout` | JWT | `{}` 或 `{"refreshToken":"..."}`(模板发 `{}`;改造后发 refreshToken) | `true`(撤销指定 refresh;缺省撤销该用户全部) | 任何错误也返回 200/`true`(前端 `needTip:false` 且忽略) |
| 4 | `GET /api/user/detail` | JWT | — | `{"id":1,"username":"admin","enable":true,"createTime":"<ISO>","updateTime":"<ISO>","profile":{"id":1,"userId":1,"nickName":"管理员","gender":0,"avatar":"/admin/avatar.svg","address":null,"email":null},"roles":[{"id":1,"code":"SUPER_ADMIN","name":"超级管理员","enable":true}],"currentRole":{"id":1,"code":"SUPER_ADMIN","name":"超级管理员","enable":true}}` | `401` |
| 5 | `GET /api/role/permissions/tree` | JWT | — | §12.3 的固定菜单树数组(服务端常量,不入库) | `401` |
| 6 | `GET /api/permission/menu/validate?path=<string>` | JWT | query `path`(可能是 `/monitor/12` 这类实际值) | `true`:`path` 匹配菜单树任一 `enable:true` MENU 的 `path` 模式(按 `/` 分段,`:param` 段匹配任意非空段,段数相等);否则 `false` | `401` |
| 7 | `POST /api/auth/password` | JWT | `{"oldPassword":"...","newPassword":"..."}`(新密码 8–64) | `true`;服务端 `TokenVersion++`,使所有旧 access/refresh 失效(本响应不换发新令牌) | `400 {"code":10010,"message":"原密码错误"}`;`422 {"code":422,"message":"新密码长度须为 8–64 位"}` |
| 8 | `PATCH /api/user/profile/{id}` | JWT | `{"id":1,"nickName":"...","avatar":"...","email":"...","gender":0,"address":"..."}` 任意子集(`gender/address` 可接受但不存) | 同 #4 的对象 | `403 {"code":403,"message":"只能修改本人资料"}` 当 `{id}` ≠ 当前用户 |

401 细分与前端联动:access token 缺失 / 签名错 / `TokenVersion` 不符 → HTTP 401 + `{"code":11008,"message":"登录状态已失效"}`(前端**直接**弹"…是否重新登录");access **过期** → HTTP 401 + `{"code":401,"message":"登录已过期"}`(前端拦截器据 **HTTP 401 且 code===401** 触发一次 refresh 并重放,失败再弹框)。

不实现(返回 `404 {"code":404,"message":"接口不存在","data":null}`):`/api/auth/captcha`、`/api/auth/current-role/switch/*`、`/api/auth/role/toggle`、`GET /api/auth/refresh/token`、`/api/user`(列表/CRUD)、`/api/role*`、`/api/permission*`(除 #6)。

### 12.3 固定菜单树(服务端常量,`GET /api/role/permissions/tree` 的 `data`)

PRD §6.1 四块(总览大盘 / 节点配置 / 探针视图 / 系统设置)+ 告警 + 个人中心。`Home` 与模板 `basic-routes.js` 同名,因此 `basic-routes.js` 里 `Home` 的 `meta.title` 必须改为 `总览`(否则标签页/标题显示"首页")。

```json
[
  {"id":1,"code":"Home","name":"总览","type":"MENU","parentId":null,"path":"/","redirect":null,
   "icon":"i-fe:home","component":"/src/views/home/index.vue","layout":"","keepAlive":false,
   "show":true,"enable":true,"order":1,"children":[]},
  {"id":2,"code":"Nodes","name":"节点管理","type":"MENU","parentId":null,"path":"/nodes","redirect":null,
   "icon":"i-fe:server","component":"/src/views/nodes/index.vue","layout":"","keepAlive":true,
   "show":true,"enable":true,"order":2,"children":[]},
  {"id":3,"code":"Monitor","name":"探针视图","type":"MENU","parentId":null,"path":"/monitor","redirect":null,
   "icon":"i-fe:activity","component":"/src/views/monitor/index.vue","layout":"","keepAlive":true,
   "show":true,"enable":true,"order":3,"children":[
     {"id":31,"code":"MonitorDetail","name":"节点详情","type":"MENU","parentId":3,"path":"/monitor/:id","redirect":null,
      "icon":"i-fe:bar-chart-2","component":"/src/views/monitor/detail.vue","layout":"","keepAlive":false,
      "show":false,"enable":true,"order":1,"children":[]}]},
  {"id":4,"code":"Alerts","name":"告警","type":"MENU","parentId":null,"path":"/alerts","redirect":null,
   "icon":"i-fe:bell","component":"/src/views/alerts/index.vue","layout":"","keepAlive":false,
   "show":true,"enable":true,"order":4,"children":[]},
  {"id":5,"code":"Settings","name":"系统设置","type":"MENU","parentId":null,"path":"/settings","redirect":null,
   "icon":"i-fe:settings","component":"/src/views/settings/index.vue","layout":"","keepAlive":false,
   "show":true,"enable":true,"order":5,"children":[]},
  {"id":6,"code":"Profile","name":"个人中心","type":"MENU","parentId":null,"path":"/profile","redirect":null,
   "icon":"i-fe:user","component":"/src/views/profile/index.vue","layout":"","keepAlive":false,
   "show":false,"enable":true,"order":99,"children":[]}
]
```

说明:`Nodes`(CRUD / 流量限额与重置日 / 续费信息 / 一键安装脚本)与 `Monitor`(国旗、CPU/内存微型进度条、全量 IP、ECharts 24h/7d/30d)按 PRD 分开;`MonitorDetail` 隐藏但注册,侧栏高亮"探针视图",每个节点一个标签页(`fullPath` 为 key,标题"节点详情");`Profile` 隐藏,靠头像下拉进入(`accessRoutes` 里有 `/profile` 即显示该项);通知渠道与全局阈值放在系统设置页签(PRD §6.1),不单列菜单。`keepAlive:true` 的 `nodes/index.vue`、`monitor/index.vue` 必须 `defineOptions({ name: 'Nodes' })` / `defineOptions({ name: 'Monitor' })`。图标全部来自 feather 集合(已在 safelist)。不带 BUTTON,新页面不用 `v-permission`。若前端最终把 `monitor/detail.vue` 换成查询串(`/monitor/detail?id=`),则把 `path` 改成 `/monitor/detail`,其余不变。

### 12.4 前端改造清单

**修改**

| 文件 | 改动 |
|---|---|
| `package.json` | `name: "snm-admin"`;删 `postinstall`、`up` 脚本与 `simple-git-hooks` / `lint-staged` 配置块;删 devDeps `simple-git-hooks lint-staged taze esno vue3-intro-step rollup-plugin-visualizer`;删 deps `xlsx`;加 deps `"@microsoft/signalr": "10.0.11"`、`"@microsoft/signalr-protocol-msgpack": "10.0.11"`(精确版本,无 `^`) |
| `.env` | `VITE_TITLE = 'Server Node Monitor'`、`VITE_USE_HASH = 'false'`、`VITE_PUBLIC_PATH = '/admin/'`、`VITE_AXIOS_BASE_URL = '/api'` |
| `.env.development` | 只保留 `VITE_PROXY_TARGET = 'http://127.0.0.1:5080'`(其余继承 `.env`;**删 apifox 行与 `VITE_USE_HASH='true'`**) |
| `.env.production` | 删除文件,或只留注释(**必须删掉 apifox 行**,否则覆盖 `.env`) |
| `vite.config.js` | `server.proxy` 替换为 §11.3;删 `/runapi`、`optimizeDeps`;加 `build.outDir` / `emptyOutDir`;import 改为 `./build/plugin-isme/index.js`(顺手把 `build/plugin-isme/*.js` 内的 `./icons`、`..` 补成 `./icons.js`、`../index.js`,消掉 native loader 警告) |
| `index.html` | `lang="zh-CN"`;其余保留(loading 骨架、`%VITE_TITLE%`) |
| `src/settings.js` | `basePermissions = []`;`defaultPrimaryColor` 与 `naiveThemeOverrides.common` 改为项目主色(可选);`layoutSettingVisible` 保留 `true` |
| `src/api/index.js` | 保留 `getUser / getRolePermissions / validateMenuPath`;`refreshToken: data => request.post('/auth/refresh', data, { needToken: false, needTip: false })`;`logout: data => request.post('/auth/logout', data ?? {}, { needTip: false })`;删 `switchCurrentRole` |
| `src/store/modules/auth.js` | state `{ accessToken: undefined, refreshToken: undefined }`;`setToken({ accessToken, refreshToken })` 两者都存(`refreshToken` 缺省时保留旧值);`persist: { key: 'snm_auth' }`;删 `switchCurrentRole`;`logout()` 先 `api.logout({ refreshToken })`(catch 忽略)再 `resetLoginState()` + `toLogin()` |
| `src/utils/http/interceptors.js` | `resReject` 增加分支:`status === 401 && (data?.code ?? 401) === 401 && !config._retried && !/\/auth\/(login\|refresh)$/.test(config.url) && useAuthStore().refreshToken` → `const token = await refreshOnce()`(模块级单飞 Promise:并发 401 共享同一次刷新;成功 `authStore.setToken(...)`,失败 `authStore.resetToken()` 并 `throw`)→ `config._retried = true; config.headers.Authorization = 'Bearer ' + token; return axiosInstance(config)`;刷新失败则继续走原 `resolveResError(401)` 弹框。`refreshOnce` 用裸 `axios.post(import.meta.env.VITE_AXIOS_BASE_URL + '/auth/refresh', { refreshToken })`,**不经拦截器**,避免递归 |
| `src/utils/http/helpers.js` | 403/404/500 分支改为 `message = message ?? '<原固定文案>'`(优先服务端文案);新增 `case 429: message = message ?? '请求过于频繁,请稍后再试'`;无网络时把 axios 英文 message 映射为"网络异常,请检查连接" |
| `src/router/guards/permission-guard.js` | 无 token 分支 `{ path: 'login', ... }` → `{ path: '/login', ... }`(相对路径隐患);`WHITE_LIST` 加 `'/403'` |
| `src/router/basic-routes.js` | `Home` 的 `meta.title: '总览'`(需要 KeepAlive 时在此加 `keepAlive: true` 并给组件 `name: 'Home'`) |
| `src/views/login/index.vue` | 删验证码输入/图片/`initCaptcha`/`captchaUrl`、"一键体验"按钮与 `quickLogin`、`isQuick`/`captcha` 参数与 `10003` 分支;`lStorage` 只记 `username`(**不再存密码**);`api.login({ username, password })`;标题/背景换项目素材(删 `login_banner/login_bg` 引用) |
| `src/views/login/api.js` | 仅 `login: data => request.post('/auth/login', data, { needToken: false })` |
| `src/views/home/index.vue` | 重写为总览大盘(资产总数 / 离线预警 / 即将到期 / MRR + 实时概览 + 告警);ECharts 引用改走 `src/utils/echarts.js` |
| `src/views/profile/index.vue`、`api.js` | 保留修改密码(成功后 `$message.success('密码已修改,请重新登录')` → `authStore.logout()`,与服务端 `TokenVersion++` 配合)与昵称/头像修改;性别/地址可删 |
| `src/layouts/components/UserAvatar.vue` | 删"切换角色"项、`RoleSelect` 引用与 `toggleRole` 分支;`[currentRole.name]` 可保留(显示"超级管理员") |
| `src/layouts/components/index.js` | 删 `RoleSelect`、`BeginnerGuide` 导出 |
| `src/layouts/normal/header/index.vue`、`src/layouts/full/header/index.vue` | 删 `<BeginnerGuide />`、GitHub/Gitee 图标与 `handleLinkClick`;可插入实时连接指示组件 |
| `src/components/common/TheFooter.vue`、`TheLogo.vue`、`src/layouts/components/SideLogo.vue` | 品牌文案 / Logo 替换 |
| `src/components/me/crud/index.vue` | 删 `xlsx` import、`handleExport` 及 `defineExpose` 中的引用 |
| `src/assets/icons/dynamic-icons.js` | 清空为 `[]`(原 `i-simple-icons:juejin`),需要非 feather 的动态图标时再加 |
| `README.md` | 替换为项目说明 |

**删除**:§8 列表。**新增**:`src/views/{nodes,monitor,alerts,settings}/**`(各含 `index.vue` + `api.js`,`monitor/detail.vue`)、`src/utils/echarts.js`、`src/utils/format.js`、`src/realtime/*`(SignalR 单例 + Pinia live store)、`public/avatar.svg`、项目 Logo(`src/assets/images/logo.svg` 或 png)。

### 12.5 环境 / Vite 值汇总

| 变量 | dev(`.env` + `.env.development`) | prod(`.env`) |
|---|---|---|
| `VITE_TITLE` | `Server Node Monitor` | 同 |
| `VITE_USE_HASH` | `false` | `false` |
| `VITE_PUBLIC_PATH` | `/admin/` | `/admin/` |
| `VITE_AXIOS_BASE_URL` | `/api` | `/api` |
| `VITE_PROXY_TARGET` | `http://127.0.0.1:5080` | (不用) |
| `build.outDir` | — | `../../src/SNM.Master/wwwroot/admin`(`emptyOutDir: true`) |
| dev URL | `http://localhost:3200/admin/` | `http://127.0.0.1:5080/admin/` |
| 构建命令 | `npm run dev` | `npm ci --no-audit --no-fund && npm run build`(Git Bash 下若用 env 覆盖须 `MSYS_NO_PATHCONV=1`) |

### 12.6 对候选方案 A / B 的核对与纠正

共同正确之处(已由源码 / 构建证实):兼容 + 少量改造的策略;`VITE_PUBLIC_PATH='/admin/'` + history;`/api` 代理去 rewrite、`/hubs` 加 `ws:true`;`basePermissions=[]`;删 postinstall;登录页删验证码/一键体验且不再存密码;`Home` 同名不重复注册;`component` 必须存在于 `src/views/**`;分页 `pageNo/pageSize → {pageData,total}`;`refreshToken` 改为 `POST /api/auth/refresh` 并在拦截器单飞刷新;`.npmrc` 镜像保留(实测可用)。

需要纠正 / 补充:

| 条目 | 问题 | 纠正 |
|---|---|---|
| A / B 均未点明 | 模板前端**没有**任何 refresh 调用(`api.refreshToken` 是死代码,守卫/拦截器都不用它),二者的"改造"实际是"从零新增"整条刷新链路 | 按 §12.4 interceptors 行实现;`refreshOnce` 必须绕过拦截器 |
| A / B 均未提 | `postinstall` 已实际把 `pre-commit`(`npx lint-staged`)写进仓库根 `.git/hooks`(§1.3),根目录无 package.json 会让 `git commit` 失败 | 编排者删除 `.git/hooks/pre-commit`(或 `SKIP_SIMPLE_GIT_HOOKS=1` / `--no-verify`);前端首个任务删 postinstall |
| A / B 均未提 | Git Bash MSYS 路径转换会毁掉 `VITE_PUBLIC_PATH=/admin/` 环境变量 | 值写 `.env`;脚本加 `MSYS_NO_PATHCONV=1` |
| A / B 均未提 | 守卫 `{ path: 'login' }` 相对路径隐患;`WHITE_LIST` 无 `/403` | §12.4 |
| A / B 均未提 | Master 默认 ProblemDetails / 204 会被拦截器误判为"未知异常" / 空数据 | 统一信封,禁 204 |
| A API §2.7 / B API §2.6 的 `keepAlive:true` | 仅当组件 `defineOptions({ name: code })` 时 KeepAlive 才生效;A 给 `Home` 设 true,但 `Home` 路由来自 `basic-routes.js`,树里的 `keepAlive/name` 对它不生效 | 页面加 `name`;首页 keepAlive 写在 `basic-routes.js` meta;`basic-routes` 的 `Home.meta.title` 改"总览"(A/B 都改了,正确) |
| A API §2.4 / B API §2.5 用户信息 | 形状正确;B 的 `avatar:"/admin/avatar.svg"` 要求 `web/admin/public/avatar.svg` 存在(构建后位于 `wwwroot/admin/avatar.svg`) | 新增该文件 |
| B API §2.6 菜单树 | `AlertCenter` 父项 `path:""、component:""`:前端 `route.path` 为空 → 不注册路由、只作分组,可行;BUTTON 子项会让 `v-permission` 生效,但单管理员无必要,且树未列的 code 会导致元素被移除 | 采用 §12.3 扁平树,不带 BUTTON |
| B API §2.4 改密后 `TokenVersion++` | 当前会话也失效,前端在下一次请求收到 `11008` 才弹框;体验可接受,但应在 profile 页成功后主动 `logout()` 给出明确提示 | §12.4 profile 行 |
| A FRONTEND §2.2 删 `src/views/iframe/**` | 可以,但 `permission.js generateRoute` 仍硬编码 `/src/views/iframe/index.vue`;只要树无外链即无影响 | 保留说明即可 |
| A / B 图标 | 均用 `i-fe:*`,正确;将来菜单若要 `i-carbon:*` 等,必须加进 `dynamic-icons.js` | §5.1 icon 行 |
| A / B 关于 `validateMenuPath` | 守卫只在 `to.name` 无匹配路由时才调用它,所有已注册路由都不会触发;Master 实现为简单模式匹配即可 | §12.2 #6 |
| B FRONTEND §1 "xlsx 保留" | 可行但白占 384 KB 的 chunk;A 删除更合理 | 删 |
| C(API.md)`POST /api/auth/refresh/token` | 路径与 A / B 的 `/auth/refresh` 不一致 | 定为 `POST /api/auth/refresh` |
| A FRONTEND §1.3 "`.npmrc` 保留 npmmirror" | 正确,实测 22 s | — |
| B FRONTEND §2.1 `layoutSettingVisible=false` | 纯偏好;保留 true 不影响功能 | 任选 |

---

## 13. 附:mock 实测原始响应索引

- `POST /auth/login` → `{"code":0,"message":"OK","data":{"accessToken":"access-token:admin:super-admin"}}`(mock 无 refreshToken)。
- `GET /user/detail` → §6 全文。
- `GET /role/permissions/tree` → 4 个根节点(`SysMgt` / `Demo` / `UserProfile` / `Base`),字段全集见 §5.1;`RoleUser` 示范 `path:"/pms/role/user/:roleId"`、`layout:"full"`、`show:false` 的隐藏详情页;`UserProfile` 示范根级隐藏项 `path:"/profile"`、`show:false`、`order:99`。
- `GET /permission/menu/tree`、`GET /permission/tree` → 同结构全量树(pms 用)。
- `POST /auth/logout` → `{"code":0,"message":"OK","data":true}`。
- `POST /auth/current-role/switch/SUPER_ADMIN` → `{"code":0,"message":"OK","data":{"accessToken":"access-token:admin:super-admin"}}`。
- `GET /auth/captcha` → `image/svg+xml`。
- `GET /role/page`、`GET /user`、`GET /role?enable=1`、`GET /permission/button/1` → 见 §4.3。
- 404(mock 未定义,`{"apifoxError":{"code":404,...}}`):`GET /auth/refresh/token`、`GET /permission/menu/validate`、`POST /auth/password`、`PATCH /user/profile/1`、`POST /auth/role/toggle`;写操作 → `400 {"code":30001,"message":"预览环境不支持此操作"}`。
- 所有 mock 响应额外带 `"originUrl"` 字段与 `Success: false` 响应头(Apifox 特性,忽略)。
