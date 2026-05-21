<%@ Page Language="C#" AutoEventWireup="true" %>
<%@ Assembly Name="System.DirectoryServices, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" %>
<%@ Import Namespace="System.DirectoryServices" %>
<%@ Import Namespace="System.Web.Configuration" %>

<script runat="server">
    protected void Page_Load(object sender, EventArgs e)
    {
        // ── Error log endpoint ────────────────────────────────────────────────────
        if (Request.HttpMethod == "POST" && Request.Headers["X-Log-Error"] != null)
        {
            try
            {
                using (var sr = new System.IO.StreamReader(Request.InputStream))
                {
                    string msg = sr.ReadToEnd();
                    string user = Session["Username"]   != null ? Session["Username"].ToString()   : "Unknown";
                    string dom  = Session["UserDomain"] != null ? Session["UserDomain"].ToString() : "Unknown";
                    string dir  = Server.MapPath("~/Logs");
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                    string path = System.IO.Path.Combine(dir, "error_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    System.IO.File.AppendAllText(path, string.Format(
                        "[{0}] USER: {1} ({2}){3}{4}{3}----------------------------------------{3}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), user, dom, Environment.NewLine, msg));
                }
            }
            catch { }
            Response.Clear(); Response.Write("OK"); Response.End(); return;
        }

        // ── Skills endpoints ──────────────────────────────────────────────────────
        // GET with X-List-Skills  → JSON array of available skills (id/name/description)
        // POST with X-Load-Skill  → plain text system prompt for the given skill id
        if (Request.Headers["X-List-Skills"] != null || Request.Headers["X-Load-Skill"] != null)
        {
            if (Session["UserAuthenticated"] == null || !(bool)Session["UserAuthenticated"])
            { Response.StatusCode = 401; Response.End(); return; }
            try
            {
                string skillsDir = Server.MapPath("~/Skills");
                if (!System.IO.Directory.Exists(skillsDir))
                    System.IO.Directory.CreateDirectory(skillsDir);

                if (Request.Headers["X-List-Skills"] != null)
                {
                    // Return [{id, name, description, icon}]
                    var sb = new System.Text.StringBuilder("[");
                    bool first = true;
                    foreach (string file in System.IO.Directory.GetFiles(skillsDir, "*.md"))
                    {
                        string id = System.IO.Path.GetFileNameWithoutExtension(file).ToLower();
                        string raw = System.IO.File.ReadAllText(file, System.Text.Encoding.UTF8);
                        string name = id, desc = "", icon = "sparkles";
                        if (raw.StartsWith("---"))
                        {
                            int end = raw.IndexOf("---", 3);
                            if (end > 0)
                            {
                                foreach (string line in raw.Substring(3, end - 3).Split('\n'))
                                {
                                    int colon = line.IndexOf(':');
                                    if (colon < 0) continue;
                                    string k = line.Substring(0, colon).Trim().ToLower();
                                    string v = line.Substring(colon + 1).Trim();
                                    if (k == "name")        name = v;
                                    if (k == "description") desc = v;
                                    if (k == "icon")        icon = v;
                                }
                            }
                        }
                        if (!first) sb.Append(","); first = false;
                        sb.AppendFormat("{{\"id\":\"{0}\",\"name\":\"{1}\",\"description\":\"{2}\",\"icon\":\"{3}\"}}",
                            id.Replace("\"","\\\""), name.Replace("\"","\\\""),
                            desc.Replace("\"","\\\""), icon.Replace("\"","\\\""));
                    }
                    sb.Append("]");
                    Response.ContentType = "application/json";
                    Response.Write(sb.ToString());
                }
                else // X-Load-Skill
                {
                    string skillId = Request.Headers["X-Load-Skill"];
                    // Sanitise: only letters, digits, hyphens, underscores
                    if (!System.Text.RegularExpressions.Regex.IsMatch(skillId, @"^[a-zA-Z0-9_-]+$"))
                        throw new Exception("Invalid skill id");
                    string skillFile = System.IO.Path.Combine(skillsDir, skillId + ".md");
                    if (!System.IO.File.Exists(skillFile))
                    { Response.StatusCode = 404; Response.Write("Skill not found"); Response.End(); return; }
                    string content = System.IO.File.ReadAllText(skillFile, System.Text.Encoding.UTF8);
                    // Strip frontmatter — return only the system prompt body
                    if (content.TrimStart().StartsWith("---"))
                    {
                        content = content.TrimStart();
                        int end = content.IndexOf("---", 3);
                        if (end > 0) content = content.Substring(end + 3).TrimStart();
                    }
                    Response.ContentType = "text/plain; charset=utf-8";
                    Response.Write(content);
                }
            }
            catch (Exception ex)
            {
                Response.StatusCode = 500;
                Response.Write("{\"error\":\"" + ex.Message.Replace("\"","\\\"") + "\"}");
            }
            Response.End(); return;
        }

        // ── Tool proxy endpoint (agentic CORS bypass) ────────────────────────────
        if (Request.HttpMethod == "POST" && Request.Headers["X-Tool-Proxy"] != null)
        {
            if (Session["UserAuthenticated"] == null || !(bool)Session["UserAuthenticated"])
            { Response.StatusCode = 401; Response.End(); return; }
            try
            {
                string json;
                using (var sr = new System.IO.StreamReader(Request.InputStream)) json = sr.ReadToEnd();
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var req = ser.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);

                string method      = req.ContainsKey("method")      ? req["method"].ToUpper() : "GET";
                string url         = req.ContainsKey("url")         ? req["url"]              : "";
                string body        = req.ContainsKey("body")        ? req["body"]             : null;
                string contentType = req.ContainsKey("contentType") ? req["contentType"]      : "application/json";

                if (string.IsNullOrEmpty(url)) throw new Exception("url is required");

                var httpReq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                httpReq.Method = method; httpReq.Timeout = 10000;
                httpReq.UserAgent = "SET-LLM-Agent/1.0";

                if (method != "GET" && !string.IsNullOrEmpty(body))
                {
                    httpReq.ContentType = contentType;
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    httpReq.ContentLength = bytes.Length;
                    using (var s = httpReq.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }

                using (var resp = (System.Net.HttpWebResponse)httpReq.GetResponse())
                using (var sr   = new System.IO.StreamReader(resp.GetResponseStream()))
                {
                    Response.ContentType = "application/json";
                    Response.Write(sr.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                Response.StatusCode = 500; Response.ContentType = "application/json";
                Response.Write("{\"error\":\"" + ex.Message.Replace("\\","\\\\").Replace("\"","\\\"") + "\"}");
            }
            Response.End(); return;
        }

        // ── Auth ────────────────────────────────────────────────────────────────
        if (Session["UserAuthenticated"] != null && (bool)Session["UserAuthenticated"])
        {
            ChatPanel.Visible = true; LoginPanel.Visible = false;
            UsernameLiteral.Text  = Server.HtmlEncode(Session["Username"]   != null ? Session["Username"].ToString()   : "");
            UserDomainLiteral.Text = Server.HtmlEncode(Session["UserDomain"] != null ? Session["UserDomain"].ToString() : "");
            EndpointsJsonLiteral.Text = WebConfigurationManager.AppSettings["LLM_Endpoints"] ?? "[]";
        }
        else
        {
            ChatPanel.Visible = false; LoginPanel.Visible = true;
            if (!IsPostBack)
            {
                string names = WebConfigurationManager.AppSettings["LDAP_Names"];
                if (!string.IsNullOrEmpty(names)) { DomainSelect.DataSource = names.Split(';'); DomainSelect.DataBind(); }
            }
        }
    }

    protected void LoginButton_Click(object sender, EventArgs e)
    {
        string username = UsernameInput.Text.Trim();
        string password = PasswordInput.Text;
        string domain   = DomainSelect.SelectedValue;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        { ErrorMessage.Text = "Username and password are required."; ErrorMessage.Visible = true; return; }

        bool bypass = (WebConfigurationManager.AppSettings["Bypass_LDAP"] ?? "").ToLower() == "true";
        if (bypass)
        { Session["UserAuthenticated"] = true; Session["Username"] = username; Session["UserDomain"] = domain; Response.Redirect(Request.RawUrl); return; }

        string ldapPath   = WebConfigurationManager.AppSettings["LDAPPATH_"   + domain];
        string ldapDomain = WebConfigurationManager.AppSettings["LDAPDOMAIN_" + domain];

        if (string.IsNullOrEmpty(ldapPath) || string.IsNullOrEmpty(ldapDomain))
        { ErrorMessage.Text = "LDAP configuration for the selected domain was not found."; ErrorMessage.Visible = true; return; }

        if (AuthenticateUser(ldapDomain, ldapPath, username, password))
        { Session["UserAuthenticated"] = true; Session["Username"] = username; Session["UserDomain"] = domain; Response.Redirect(Request.RawUrl); }
        else
        { ErrorMessage.Text = "Invalid username or password."; ErrorMessage.Visible = true; }
    }

    private bool AuthenticateUser(string domain, string path, string username, string password)
    {
        try
        {
            using (var entry = new DirectoryEntry(path, domain + @"\" + username, password))
            { object obj = entry.NativeObject; return true; }
        }
        catch { return false; }
    }

    protected void LogoutButton_Click(object sender, EventArgs e)
    { Session.Clear(); Response.Redirect(Request.RawUrl); }
</script>

<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0, viewport-fit=cover">
  <title>SET LLM</title>
  <!-- Apply saved theme before render to prevent flash -->
  <script>(function(){var t=localStorage.getItem('setllm-theme')||'dark';document.documentElement.setAttribute('data-theme',t);})();</script>
  <link id="hljs-theme" rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@highlightjs/cdn-assets@11/styles/github-dark.min.css">
  <script src="https://cdn.jsdelivr.net/npm/@tailwindcss/browser@4"></script>
  <script src="https://cdn.jsdelivr.net/npm/marked@14/marked.min.js"></script>
  <script src="https://cdn.jsdelivr.net/npm/@highlightjs/cdn-assets@11/highlight.min.js"></script>
  <script defer src="https://cdn.jsdelivr.net/npm/alpinejs@3.x.x/dist/cdn.min.js"></script>
  <style type="text/tailwindcss">
    @theme {
      --color-bg:         oklch(14% 0.02 275);
      --color-surface:    oklch(20% 0.025 275);
      --color-surface-hi: oklch(26% 0.03 275);
      --color-border:     oklch(32% 0.02 275);
      --color-accent:     oklch(60% 0.22 290);
      --color-accent-hi:  oklch(72% 0.18 290);
      --color-text:       oklch(94% 0.015 275);
      --color-mute:       oklch(62% 0.02 275);
      --color-mute-2:     oklch(48% 0.02 275);
    }
  </style>
  <style>
    /* ── Light theme overrides ── */
    [data-theme="light"] {
      --color-bg:         oklch(97% 0.008 275);
      --color-surface:    oklch(92% 0.012 275);
      --color-surface-hi: oklch(86% 0.015 275);
      --color-border:     oklch(78% 0.015 275);
      --color-accent:     oklch(52% 0.22 290);
      --color-accent-hi:  oklch(42% 0.22 290);
      --color-text:       oklch(12% 0.02 275);
      --color-mute:       oklch(40% 0.02 275);
      --color-mute-2:     oklch(55% 0.02 275);
    }
    [data-theme="light"] .hljs            { background: oklch(88% 0.01 275) !important; }
    [data-theme="light"] .code-block-wrapper { border-color: oklch(76% 0.015 275); }
    [data-theme="light"] .code-lang-bar   { background: oklch(83% 0.012 275); color: oklch(38% 0.02 275); }
    [data-theme="light"] .copy-btn        { border-color: oklch(65% 0.02 275); color: oklch(40% 0.02 275); }
    [data-theme="light"] .copy-btn:hover  { background: oklch(76% 0.015 275); color: oklch(15% 0.02 275); }
    [data-theme="light"] .prose :not(.code-block-wrapper)>code { background: oklch(85% 0.01 275); }
    [data-theme="light"] .prose th        { background: oklch(86% 0.015 275); }

    [x-cloak] { display: none !important; }
    body { font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; }
    .scrollbar-thin::-webkit-scrollbar { width: 8px; height: 8px; }
    .scrollbar-thin::-webkit-scrollbar-track { background: transparent; }
    .scrollbar-thin::-webkit-scrollbar-thumb { background: oklch(35% 0.02 275); border-radius: 4px; }
    .scrollbar-thin::-webkit-scrollbar-thumb:hover { background: oklch(45% 0.04 290); }
    [data-theme="light"] .scrollbar-thin::-webkit-scrollbar-thumb { background: oklch(70% 0.02 275); }
    @keyframes blink { 50% { opacity: 0; } }
    .cursor-blink::after {
      content: ''; display: inline-block; width: 6px; height: 1em;
      background: currentColor; vertical-align: text-bottom;
      margin-left: 2px; animation: blink .8s step-end infinite;
    }
    .code-block-wrapper { position: relative; margin: .6em 0; border-radius: 8px; overflow: hidden; border: 1px solid oklch(32% 0.02 275); }
    .code-lang-bar { display: flex; justify-content: space-between; align-items: center; background: oklch(18% 0.025 275); padding: 3px 12px; font-size: 11px; color: oklch(55% 0.02 275); font-family: monospace; letter-spacing: .04em; }
    .copy-btn { background: transparent; border: 1px solid oklch(38% 0.02 275); color: oklch(62% 0.02 275); border-radius: 4px; padding: 2px 10px; font-size: 11px; cursor: pointer; transition: all .15s; font-family: inherit; }
    .copy-btn:hover { background: oklch(30% 0.03 275); color: oklch(88% 0.02 275); }
    .hljs { background: oklch(11% 0.02 275) !important; }
    .code-block-wrapper pre { margin: 0; overflow-x: auto; }
    .code-block-wrapper pre code { padding: .9em 1em; display: block; }
    .prose { line-height: 1.7; }
    .prose p { margin: .5em 0; } .prose p:first-child { margin-top: 0; } .prose p:last-child { margin-bottom: 0; }
    .prose h1,.prose h2,.prose h3,.prose h4 { font-weight: 600; line-height: 1.3; margin: .9em 0 .4em; }
    .prose h1 { font-size: 1.35em; } .prose h2 { font-size: 1.2em; } .prose h3 { font-size: 1.05em; }
    .prose ul,.prose ol { padding-left: 1.4em; margin: .5em 0; } .prose li { margin: .2em 0; }
    .prose :not(.code-block-wrapper)>code { background: oklch(11% 0.02 275); padding: 1px 5px; border-radius: 3px; font-family: ui-monospace,monospace; font-size: .85em; }
    .prose blockquote { border-left: 3px solid oklch(48% 0.08 290); padding-left: .9em; margin: .7em 0; color: oklch(70% 0.02 275); }
    .prose a { color: oklch(68% 0.18 290); text-decoration: underline; }
    .prose table { border-collapse: collapse; width: 100%; margin: .7em 0; font-size: .9em; }
    .prose th,.prose td { border: 1px solid oklch(32% 0.02 275); padding: 4px 10px; }
    .prose th { background: oklch(22% 0.025 275); font-weight: 600; }
    .prose hr { border-color: oklch(32% 0.02 275); margin: .9em 0; }
    .prose strong { font-weight: 600; color: oklch(96% 0.015 275); }
    @view-transition { navigation: auto; }
  </style>
</head>
<body class="bg-[var(--color-bg)] text-[var(--color-text)] h-dvh overflow-hidden antialiased">
<form id="form1" runat="server" class="h-full">

  <!-- ── LOGIN ── -->
  <asp:Panel ID="LoginPanel" runat="server">
    <div class="min-h-dvh flex items-center justify-center p-6 bg-gradient-to-br from-[var(--color-bg)] via-[oklch(16%_0.04_290)] to-[oklch(20%_0.08_290)]">
      <div class="w-full max-w-md bg-[var(--color-surface)]/85 backdrop-blur-xl border border-[var(--color-border)] rounded-2xl p-10 shadow-[0_25px_60px_-15px_rgba(0,0,0,0.6)]">
        <svg class="w-20 h-auto block mx-auto mb-4" xmlns="http://www.w3.org/2000/svg" viewBox="740 0 270 450">
          <path d="M 809.5 25 L 859.5 75 L 809.5 125 L 759.5 75 Z" fill="#E13B07"/>
          <path d="M 876.5 92 L 926.5 142 L 876.5 192 L 826.5 142 Z" fill="#F37000"/>
          <path d="M 944.5 160 L 994.5 210 L 944.5 260 L 894.5 210 Z" fill="#462C83"/>
          <path d="M 874.5 226 L 924.5 276 L 874.5 326 L 824.5 276 Z" fill="#005CA9"/>
          <path d="M 806.5 294 L 856.5 344 L 806.5 394 L 756.5 344 Z" fill="#13531B"/>
        </svg>
        <h1 class="text-2xl font-bold text-center text-[var(--color-accent-hi)] tracking-tight">SET LLM</h1>
        <p class="text-[11px] text-center text-[var(--color-mute)] uppercase tracking-[.15em] font-semibold mt-1 mb-6">Active Directory Sign In</p>
        <asp:Label ID="ErrorMessage" runat="server" CssClass="block bg-red-500/10 border border-red-500/25 text-red-400 px-3.5 py-2.5 rounded-lg text-sm text-center mb-5" Visible="false" />
        <div class="mb-4">
          <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider font-semibold mb-1.5">Username</label>
          <asp:TextBox ID="UsernameInput" runat="server" CssClass="w-full bg-[var(--color-bg)] border border-[var(--color-border)] rounded-lg px-3.5 py-2.5 text-sm outline-none focus:border-[var(--color-accent)] focus:ring-2 focus:ring-[var(--color-accent)]/20 transition" placeholder="e.g. johndoe" />
        </div>
        <div class="mb-4">
          <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider font-semibold mb-1.5">Password</label>
          <asp:TextBox ID="PasswordInput" runat="server" TextMode="Password" CssClass="w-full bg-[var(--color-bg)] border border-[var(--color-border)] rounded-lg px-3.5 py-2.5 text-sm outline-none focus:border-[var(--color-accent)] focus:ring-2 focus:ring-[var(--color-accent)]/20 transition" placeholder="********" />
        </div>
        <div class="mb-5">
          <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider font-semibold mb-1.5">Domain</label>
          <asp:DropDownList ID="DomainSelect" runat="server" CssClass="w-full bg-[var(--color-bg)] border border-[var(--color-border)] rounded-lg px-3.5 py-2.5 text-sm outline-none focus:border-[var(--color-accent)] focus:ring-2 focus:ring-[var(--color-accent)]/20 transition" />
        </div>
        <asp:Button ID="LoginButton" runat="server" Text="Sign In" CssClass="w-full bg-[var(--color-accent)] hover:bg-[var(--color-accent-hi)] text-white rounded-lg py-2.5 text-sm font-semibold transition-colors cursor-pointer" OnClick="LoginButton_Click" />
      </div>
    </div>
  </asp:Panel>

  <!-- ── CHAT ── -->
  <asp:Panel ID="ChatPanel" runat="server" Visible="false">
    <div x-data="setLlmApp()" x-init="init()" class="h-dvh flex flex-col" x-cloak>

      <!-- Header -->
      <header class="h-14 flex items-center gap-3 px-4 bg-[var(--color-surface)] border-b border-[var(--color-border)] shrink-0">
        <button type="button" @click="historyOpen = !historyOpen" class="p-1.5 rounded-md hover:bg-[var(--color-surface-hi)] text-[var(--color-mute)] hover:text-[var(--color-text)] transition">
          <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M4 6h16M4 12h16M4 18h16"/></svg>
        </button>
        <svg class="h-8 w-auto" xmlns="http://www.w3.org/2000/svg" viewBox="740 0 270 450">
          <path d="M 809.5 25 L 859.5 75 L 809.5 125 L 759.5 75 Z" fill="#E13B07"/>
          <path d="M 876.5 92 L 926.5 142 L 876.5 192 L 826.5 142 Z" fill="#F37000"/>
          <path d="M 944.5 160 L 994.5 210 L 944.5 260 L 894.5 210 Z" fill="#462C83"/>
          <path d="M 874.5 226 L 924.5 276 L 874.5 326 L 824.5 276 Z" fill="#005CA9"/>
          <path d="M 806.5 294 L 856.5 344 L 806.5 394 L 756.5 344 Z" fill="#13531B"/>
        </svg>
        <h1 class="text-base font-semibold text-[var(--color-accent-hi)] tracking-tight">SET LLM</h1>
        <div class="flex items-center gap-1.5 ml-1">
          <div class="w-2 h-2 rounded-full transition-colors" :class="statusOk === true ? 'bg-emerald-500' : statusOk === false ? 'bg-red-500 animate-pulse' : 'bg-gray-500'"></div>
          <span class="text-xs text-[var(--color-mute)]" x-text="status"></span>
        </div>
        <div x-show="agenticEnabled" class="flex items-center gap-1 bg-amber-500/15 border border-amber-500/30 text-amber-400 px-2 py-0.5 rounded-full text-[10px] font-semibold ml-1">
          <svg class="w-2.5 h-2.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z"/></svg>
          Agentic
        </div>
        <div class="flex-1"></div>

        <!-- ── Mode / Skill selector ── -->
        <div class="relative mr-2" x-data>
          <!-- Active skill badge (clickable to change) -->
          <button type="button" @click="skillsOpen = !skillsOpen"
                  :class="activeSkillId ? 'border-violet-500/60 bg-violet-500/15 text-violet-300' : 'border-[var(--color-border)] text-[var(--color-mute)] hover:border-[var(--color-accent)]'"
                  class="flex items-center gap-1.5 border rounded-md px-2.5 py-1.5 text-xs font-medium transition cursor-pointer">
            <svg class="w-3.5 h-3.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
              <path stroke-linecap="round" stroke-linejoin="round" d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z"/>
            </svg>
            <span x-text="activeSkillId ? activeSkillName : 'Mode'"></span>
            <svg x-show="activeSkillId" @click.stop="deactivateSkill()" class="w-3 h-3 text-violet-400 hover:text-red-400 transition" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="3"><path stroke-linecap="round" stroke-linejoin="round" d="M6 18L18 6M6 6l12 12"/></svg>
          </button>

          <!-- Dropdown -->
          <div x-show="skillsOpen" @click.away="skillsOpen = false" x-cloak x-transition.opacity
               class="absolute right-0 top-full mt-1.5 w-72 bg-[var(--color-surface)] border border-[var(--color-border)] rounded-xl shadow-2xl z-50 overflow-hidden">
            <div class="px-3 py-2 border-b border-[var(--color-border)]">
              <div class="text-[11px] text-[var(--color-mute)] uppercase tracking-wider font-semibold">Available Modes</div>
            </div>
            <div class="max-h-64 overflow-y-auto scrollbar-thin">
              <template x-if="skills.length === 0">
                <p class="text-xs text-[var(--color-mute-2)] text-center py-5 px-3 italic">No skill files found.<br>Add .md files to IIS/Skills/</p>
              </template>
              <template x-for="skill in skills" :key="skill.id">
                <button type="button" @click="activateSkill(skill)"
                        :class="activeSkillId === skill.id ? 'bg-violet-500/15 text-violet-200' : 'hover:bg-[var(--color-surface-hi)] text-[var(--color-text)]'"
                        class="w-full flex items-start gap-3 px-3 py-2.5 text-left transition cursor-pointer">
                  <div class="w-7 h-7 rounded-lg bg-[var(--color-surface-hi)] flex items-center justify-center shrink-0 mt-0.5">
                    <svg class="w-3.5 h-3.5 text-violet-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/></svg>
                  </div>
                  <div class="min-w-0 flex-1">
                    <div class="text-sm font-medium truncate" x-text="skill.name"></div>
                    <div class="text-[10px] text-[var(--color-mute-2)] mt-0.5 truncate" x-text="skill.description || skill.id"></div>
                  </div>
                  <svg x-show="activeSkillId === skill.id" class="w-4 h-4 text-violet-400 shrink-0 mt-1" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M5 13l4 4L19 7"/></svg>
                </button>
              </template>
            </div>
            <div class="px-3 py-2 border-t border-[var(--color-border)]">
              <button type="button" @click="loadSkills()" class="text-[10px] text-[var(--color-mute)] hover:text-[var(--color-text)] transition cursor-pointer">↺ Refresh</button>
            </div>
          </div>
        </div>

        <span class="text-sm text-[var(--color-mute)] hidden md:inline mr-2">Welcome, <asp:Literal ID="UsernameLiteral" runat="server" /> (<asp:Literal ID="UserDomainLiteral" runat="server" />)</span>
        <button type="button" @click="settingsOpen = !settingsOpen" class="flex items-center gap-1.5 bg-[var(--color-surface-hi)] hover:bg-[oklch(30%_0.05_290)] border border-[var(--color-border)] hover:border-[var(--color-accent)] px-3 py-1.5 rounded-md text-xs font-medium transition mr-2 cursor-pointer">
          <svg class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z"/><path stroke-linecap="round" stroke-linejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"/></svg>
          Settings
        </button>
        <asp:Button ID="HeaderLogoutButton" runat="server" Text="Logout" CssClass="bg-transparent border border-red-500/70 text-red-400 hover:bg-red-500/15 hover:border-red-500 px-3 py-1.5 rounded-md text-xs font-medium transition cursor-pointer" OnClick="LogoutButton_Click" />
        <!-- Language toggle -->
        <button type="button" @click="$store.ui.toggleLang()"
                class="flex items-center gap-1 bg-[var(--color-surface-hi)] hover:bg-[var(--color-surface-hi)] border border-[var(--color-border)] hover:border-[var(--color-accent)] px-2.5 py-1.5 rounded-md text-xs font-semibold transition cursor-pointer ml-1"
                :title="$store.ui.darkMode ? $store.ui.t('lang') : $store.ui.t('lang')">
          <svg class="w-3.5 h-3.5 text-[var(--color-mute)]" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M3 5h12M9 3v2m1.048 9.5A18.022 18.022 0 016.412 9m6.088 9h7M11 21l5-10 5 10M12.751 5C11.783 10.77 8.07 15.61 3 18.129"/></svg>
          <span x-text="$store.ui.t('lang')"></span>
        </button>
        <!-- Dark/Light toggle -->
        <button type="button" @click="$store.ui.toggleTheme()"
                class="flex items-center gap-1 bg-[var(--color-surface-hi)] border border-[var(--color-border)] hover:border-[var(--color-accent)] px-2.5 py-1.5 rounded-md text-xs font-medium transition cursor-pointer ml-1"
                :title="$store.ui.darkMode ? $store.ui.t('light') : $store.ui.t('dark')">
          <svg x-show="$store.ui.darkMode" class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364-6.364l-.707.707M6.343 17.657l-.707.707M17.657 17.657l-.707-.707M6.343 6.343l-.707-.707M12 7a5 5 0 110 10A5 5 0 0112 7z"/></svg>
          <svg x-show="!$store.ui.darkMode" class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z"/></svg>
        </button>
      </header>

      <div class="flex-1 flex overflow-hidden relative">

        <!-- ── History Drawer ── -->
        <aside :class="historyOpen ? 'w-72' : 'w-0'" class="transition-[width] duration-300 overflow-hidden border-r border-[var(--color-border)] bg-[var(--color-surface)] flex flex-col shrink-0">
          <div class="p-3 border-b border-[var(--color-border)] shrink-0 space-y-2">
            <button type="button" @click="newConversation()" class="w-full flex items-center justify-center gap-2 bg-[var(--color-accent)] hover:bg-[var(--color-accent-hi)] text-white py-2 rounded-md text-sm font-medium transition cursor-pointer">
              <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M12 4v16m8-8H4"/></svg>
              <span x-text="$store.ui.t('newChat')"></span>
            </button>
            <div class="relative">
              <svg class="w-3.5 h-3.5 absolute left-2.5 top-1/2 -translate-y-1/2 text-[var(--color-mute-2)]" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M21 21l-4.35-4.35M17 11A6 6 0 115 11a6 6 0 0112 0z"/></svg>
              <input x-model="searchQuery" :placeholder="$store.ui.t('search')" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md pl-8 pr-2.5 py-1.5 text-xs outline-none transition" />
            </div>
          </div>
          <div class="flex-1 overflow-y-auto scrollbar-thin p-2 space-y-1">
            <p x-show="conversations.length === 0" class="text-xs text-[var(--color-mute-2)] text-center py-8 px-3 italic" x-text="$store.ui.t('noConvs')"></p>
            <p x-show="conversations.length > 0 && sortedConversations.length === 0" class="text-xs text-[var(--color-mute-2)] text-center py-4" x-text="$store.ui.t('noMatch')"></p>
            <template x-for="conv in sortedConversations" :key="conv.id">
              <div @click="loadConversation(conv.id)"
                   :class="conv.id === currentConvId ? 'bg-[var(--color-accent)]/15 border-[var(--color-accent)]/50' : 'border-transparent hover:bg-[var(--color-surface-hi)]'"
                   class="group border rounded-md p-2.5 cursor-pointer transition"
                   x-data="{ editing: false, draft: '' }">
                <div class="flex items-start justify-between gap-2">
                  <div class="min-w-0 flex-1">
                    <div class="flex items-center gap-1.5 mb-0.5">
                      <div x-show="conv.generating" class="w-1.5 h-1.5 rounded-full bg-emerald-400 shrink-0 animate-pulse"></div>
                      <div x-show="conv.agenticEnabled && !conv.generating" class="w-1.5 h-1.5 rounded-full bg-amber-400 shrink-0" title="Agentic"></div>
                      <div x-show="conv.skillId" class="w-1.5 h-1.5 rounded-full bg-violet-400 shrink-0" :title="conv.skillName || 'Skill active'"></div>
                      <div x-show="!editing" @dblclick.stop="editing = true; draft = conv.title" class="text-sm truncate text-[var(--color-text)]" x-text="conv.title || 'Untitled'" title="Double-click to rename"></div>
                      <input x-show="editing" x-model="draft"
                             @keydown.enter.stop="renameConversation(conv.id, draft); editing = false"
                             @keydown.escape.stop="editing = false"
                             @blur.stop="renameConversation(conv.id, draft); editing = false"
                             @click.stop
                             x-effect="if (editing) { $nextTick(() => { $el.focus(); $el.select(); }) }"
                             class="text-sm bg-transparent border-b border-[var(--color-accent)] outline-none w-full min-w-0" />
                    </div>
                    <div class="text-[10px] text-[var(--color-mute-2)] flex items-center gap-2">
                      <span x-text="formatDate(conv.updatedAt)"></span>
                      <span x-show="conv.totalTokens > 0" x-text="formatTokenCount(conv.totalTokens) + ' tok'"></span>
                    </div>
                  </div>
                  <button type="button" @click.stop="deleteConversation(conv.id)" class="opacity-0 group-hover:opacity-100 text-red-400 hover:text-red-300 p-1 transition shrink-0">
                    <svg class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6M1 7h22M9 7V4a1 1 0 011-1h4a1 1 0 011 1v3"/></svg>
                  </button>
                </div>
              </div>
            </template>
          </div>
          <div class="p-2 border-t border-[var(--color-border)] text-[10px] text-[var(--color-mute-2)] text-center shrink-0">
            <span x-text="conversations.length"></span> conversation<span x-show="conversations.length !== 1">s</span> · localStorage
          </div>
        </aside>

        <!-- ── Chat Area ── -->
        <main class="flex-1 flex flex-col min-w-0 relative">

          <div id="messages-scroll" class="flex-1 overflow-y-auto scrollbar-thin">
            <div id="messages" class="px-5 py-5 flex flex-col gap-4 min-h-full">

              <template x-if="messages.length === 0">
                <div class="flex-1 flex flex-col items-center justify-center text-[var(--color-mute-2)] gap-3 py-20">
                  <svg class="w-14 h-14 opacity-40" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.5"><path stroke-linecap="round" stroke-linejoin="round" d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z"/></svg>
                  <p class="text-sm" x-text="$store.ui.t('selectEp')"></p>
                </div>
              </template>

              <template x-for="(m, idx) in messages" :key="idx">
                <!-- display:contents makes this wrapper transparent to flex layout -->
                <div style="display:contents">

                  <!-- ── Tool call block ── -->
                  <div x-show="m.role === 'tool_call'" class="self-start flex gap-2.5 max-w-2xl"
                       x-data="{ open: false }">
                    <div class="w-7 h-7 rounded-full bg-amber-900/40 text-amber-400 flex items-center justify-center shrink-0 mt-0.5">
                      <svg class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z"/><path stroke-linecap="round" stroke-linejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"/></svg>
                    </div>
                    <div class="flex-1 min-w-0">
                      <div class="bg-[oklch(18%_0.04_60)] border border-[oklch(30%_0.06_60)] rounded-xl overflow-hidden">
                        <button type="button" @click="open = !open" class="w-full flex items-center gap-2 px-3 py-2 text-left hover:bg-white/5 transition">
                          <span class="text-amber-400 font-mono text-xs font-semibold" x-text="m.toolName"></span>
                          <span class="text-[10px] text-[var(--color-mute)] truncate" x-text="'(' + Object.keys(m.args || {}).join(', ') + ')'"></span>
                          <div class="ml-auto flex items-center gap-2 shrink-0">
                            <div x-show="m.result === null" class="w-1.5 h-1.5 bg-amber-400 rounded-full animate-pulse"></div>
                            <span x-show="m.result !== null && !m.result?.error" class="text-[10px] text-emerald-400">done</span>
                            <span x-show="m.result?.error" class="text-[10px] text-red-400">error</span>
                            <svg :class="open ? 'rotate-180' : ''" class="w-3 h-3 text-[var(--color-mute)] transition-transform" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M19 9l-7 7-7-7"/></svg>
                          </div>
                        </button>
                        <div x-show="open" class="border-t border-[oklch(30%_0.06_60)]">
                          <div class="px-3 py-2">
                            <div class="text-[10px] text-[var(--color-mute)] uppercase tracking-wider mb-1">Arguments</div>
                            <pre class="text-[11px] font-mono text-[var(--color-text)] whitespace-pre-wrap break-words" x-text="JSON.stringify(m.args, null, 2)"></pre>
                          </div>
                          <template x-if="m.result !== null">
                            <div class="border-t border-[oklch(30%_0.06_60)] px-3 py-2">
                              <div class="text-[10px] uppercase tracking-wider mb-1" :class="m.result?.error ? 'text-red-400' : 'text-emerald-400'">Result</div>
                              <pre class="text-[11px] font-mono text-[var(--color-text)] whitespace-pre-wrap break-words max-h-48 overflow-y-auto scrollbar-thin" x-text="typeof m.result === 'object' ? JSON.stringify(m.result, null, 2) : String(m.result)"></pre>
                            </div>
                          </template>
                        </div>
                      </div>
                      <div class="text-[10px] text-[var(--color-mute-2)] px-1 mt-1" x-text="m.meta"></div>
                    </div>
                  </div>

                  <!-- ── Regular user / assistant message ── -->
                  <div x-show="m.role !== 'tool_call'"
                       :class="m.role === 'user' ? 'flex-row-reverse self-end' : 'self-start'"
                       class="flex gap-2.5 max-w-3xl w-fit">
                    <div :class="m.role === 'user' ? 'bg-[var(--color-accent)] text-white' : 'bg-[oklch(30%_0.08_240)] text-blue-100'"
                         class="w-7 h-7 rounded-full flex items-center justify-center text-[11px] font-semibold shrink-0 mt-0.5">
                      <span x-text="m.role === 'user' ? 'U' : 'AI'"></span>
                    </div>
                    <div class="flex flex-col gap-1 min-w-0">
                      <!-- Collapsible thinking -->
                      <template x-if="m.thinking">
                        <div x-data="{ open: true }" class="bg-[oklch(15%_0.06_240)] border-l-4 border-blue-500 rounded-r overflow-hidden">
                          <button type="button" @click="open = !open" class="w-full flex items-center gap-2 px-3 py-1.5 text-left hover:bg-white/5 transition">
                            <span class="text-[10px] uppercase tracking-wider text-blue-400 font-semibold" x-text="$store.ui.t('thinking')"></span>
                            <svg :class="open ? 'rotate-180' : ''" class="w-3 h-3 text-blue-500 ml-auto transition-transform" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M19 9l-7 7-7-7"/></svg>
                          </button>
                          <div x-show="open" class="px-3 pb-2 text-xs text-blue-300 max-h-48 overflow-y-auto scrollbar-thin whitespace-pre-wrap" x-text="m.thinking"></div>
                        </div>
                      </template>
                      <!-- Image -->
                      <template x-if="m.image">
                        <img :src="m.image" class="max-w-[280px] max-h-[280px] rounded-lg border border-[var(--color-border)]" alt="attachment" />
                      </template>
                      <!-- Bubble -->
                      <div :class="[
                             m.role === 'user'
                               ? 'bg-[oklch(35%_0.18_290)] text-[oklch(96%_0.04_290)] rounded-br-sm whitespace-pre-wrap'
                               : 'bg-[var(--color-surface-hi)] text-[var(--color-text)] rounded-bl-sm ' + (m.streaming ? 'whitespace-pre-wrap' : 'prose')
                           ]"
                           class="px-3.5 py-2.5 rounded-2xl text-sm break-words leading-relaxed"
                           x-show="m.content || m.streaming"
                           x-html="renderContent(m)">
                      </div>
                      <!-- Truncated -->
                      <div x-show="m.role === 'assistant' && m.truncated && !m.streaming"
                           class="flex items-center gap-3 mt-0.5 px-0.5">
                        <div class="flex items-center gap-2.5 bg-amber-500/10 border border-amber-500/20 rounded-xl px-3.5 py-2.5 flex-1 min-w-0">
                          <svg class="w-4 h-4 text-amber-400 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                            <path stroke-linecap="round" stroke-linejoin="round" d="M13 10V3L4 14h7v7l9-11h-7z"/>
                          </svg>
                          <div class="min-w-0 flex-1">
                            <div class="text-xs font-semibold text-amber-300" x-text="$store.ui.t('truncTitle')"></div>
                            <div class="text-[10px] text-amber-500/80 mt-0.5" x-text="$store.ui.t('truncDesc')"></div>
                          </div>
                          <button type="button" @click="continueResponse(idx)" :disabled="generating"
                                  class="flex items-center gap-1.5 bg-amber-500/20 hover:bg-amber-500/35 border border-amber-500/40 hover:border-amber-400/60 text-amber-300 hover:text-amber-200 rounded-lg px-3 py-1.5 text-xs font-semibold transition shrink-0 disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer">
                            <svg class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5">
                              <path stroke-linecap="round" stroke-linejoin="round" d="M19 14l-7 7m0 0l-7-7m7 7V3"/>
                            </svg>
                            <span x-text="$store.ui.t('contBtn')"></span>
                          </button>
                        </div>
                      </div>
                      <!-- Meta row -->
                      <div :class="m.role === 'user' ? 'justify-end' : ''" class="flex items-center gap-2 text-[10px] text-[var(--color-mute-2)] px-1">
                        <span x-text="m.meta"></span>
                        <template x-if="m.role === 'assistant' && m.content && !m.streaming">
                          <div class="flex items-center gap-1">
                            <!-- Copy -->
                            <button type="button" @click="copyMessage(idx, $event)"
                                    class="flex items-center gap-1 px-2 py-1 rounded-md hover:bg-[var(--color-surface-hi)] hover:text-[var(--color-text)] transition text-[10px] cursor-pointer">
                              <svg class="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>
                              <span x-text="$store.ui.t('copy')"></span>
                            </button>
                            <!-- Download full TXT -->
                            <button type="button" @click="downloadMessage(idx)"
                                    class="flex items-center gap-1 px-2 py-1 rounded-md hover:bg-[var(--color-surface-hi)] hover:text-[var(--color-text)] transition text-[10px] cursor-pointer">
                              <svg class="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4"/></svg>
                              <span x-text="$store.ui.t('txt')"></span>
                            </button>
                            <!-- Download code blocks only -->
                            <button type="button" @click="downloadCode(idx)"
                                    x-show="hasCode(m)"
                                    class="flex items-center gap-1 px-2 py-1 rounded-md hover:bg-[var(--color-surface-hi)] hover:text-[var(--color-accent-hi)] transition text-[10px] cursor-pointer">
                              <svg class="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4"/></svg>
                              <span x-text="$store.ui.t('code')"></span>
                            </button>
                          </div>
                        </template>
                      </div>
                    </div>
                  </div>

                </div>
              </template>
            </div>
          </div>

          <!-- Stats -->
          <div x-show="stats" x-cloak class="px-5 py-1 border-t border-[var(--color-border)] bg-[var(--color-bg)] text-[10px] text-[var(--color-mute-2)] flex gap-4 shrink-0">
            <span>TTFT: <span x-text="stats?.ttft ?? '-'"></span></span>
            <span>tok/s: <span x-text="stats?.tps ?? '-'"></span></span>
            <span>tokens: <span x-text="stats?.tokens ?? '-'"></span></span>
            <span>elapsed: <span x-text="stats?.elapsed ?? '-'"></span></span>
          </div>

          <!-- Input -->
          <div class="border-t border-[var(--color-border)] bg-[var(--color-surface)] px-4 py-3.5 shrink-0">
            <template x-if="attachedImage">
              <div class="flex items-center gap-2 bg-[var(--color-surface-hi)]/70 border border-[var(--color-border)] px-3 py-1.5 rounded-lg w-fit max-w-full mb-2">
                <img :src="attachedImage.base64" class="h-9 rounded object-cover" alt="preview" />
                <span class="text-xs text-[var(--color-mute)] truncate max-w-[150px]" x-text="attachedImage.name"></span>
                <button type="button" @click="clearImage()" class="text-red-500 hover:text-red-400 font-bold px-1 cursor-pointer">×</button>
              </div>
            </template>
            <div class="flex gap-2 items-end">
              <button type="button" @click="$refs.fileInput.click()" class="bg-[var(--color-bg)] border border-[var(--color-border)] hover:border-[var(--color-accent)] text-[var(--color-accent-hi)] rounded-xl w-11 h-11 flex items-center justify-center shrink-0 transition cursor-pointer" title="Attach image (or Ctrl+V)">
                <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z"/></svg>
              </button>
              <input type="file" x-ref="fileInput" @change="onImageSelect($event)" accept="image/*" class="hidden" />
              <textarea x-model="input" x-ref="textarea"
                        @keydown.enter="if (!$event.shiftKey) { $event.preventDefault(); sendMessage(); }"
                        @input="autoResize($event.target)"
                        @paste="onPaste($event)"
                        class="flex-1 bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] focus:ring-2 focus:ring-[var(--color-accent)]/15 rounded-xl px-3.5 py-2.5 text-sm resize-none outline-none min-h-11 max-h-40 leading-relaxed transition"
                        placeholder="Type a message... (Enter to send, Shift+Enter for newline)"
                        rows="1"></textarea>
              <button type="button" @click="generating ? stopGeneration() : sendMessage()"
                      :disabled="!generating && !activeBaseUrl"
                      :class="generating ? 'bg-red-500/15 border border-red-500 text-red-400 hover:bg-red-500/25' : 'bg-[var(--color-accent)] hover:bg-[var(--color-accent-hi)] text-white border border-transparent disabled:opacity-40 disabled:cursor-not-allowed'"
                      class="rounded-xl px-5 h-11 text-sm font-semibold shrink-0 transition cursor-pointer">
                <span x-text="generating ? $store.ui.t('stop') : $store.ui.t('send')"></span>
              </button>
            </div>
            <div class="flex justify-between items-center gap-2 mt-3 pt-3 border-t border-white/5">
              <div class="flex items-center gap-2 flex-wrap">
                <template x-for="(ep, i) in endpoints" :key="i">
                  <button type="button" @click="connectEndpoint(i)"
                          :class="activeEpIdx === i ? 'border-[var(--color-accent)] bg-[var(--color-accent)]/15 text-white' : 'border-[var(--color-border)] text-[var(--color-mute)] hover:border-[var(--color-accent-hi)] hover:text-[var(--color-text)]'"
                          class="border rounded-full px-3.5 py-1.5 text-xs font-medium transition cursor-pointer" x-text="ep.name"></button>
                </template>
                <button type="button" @click="regenerate()"
                        x-show="messages.length > 0 && messages[messages.length-1]?.role === 'assistant'"
                        :disabled="generating"
                        class="flex items-center gap-1 text-[11px] text-[var(--color-mute)] hover:text-[var(--color-text)] disabled:opacity-30 transition cursor-pointer ml-1">
                  <svg class="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>
                  <span x-text="$store.ui.t('regen')"></span>
                </button>
              </div>
              <div class="flex items-center gap-1.5 text-xs text-[var(--color-mute)] shrink-0">
                <div class="w-2 h-2 rounded-full" :class="statusOk === true ? 'bg-emerald-500' : statusOk === false ? 'bg-red-500 animate-pulse' : 'bg-gray-500'"></div>
                <span class="capitalize" x-text="status"></span>
              </div>
            </div>
          </div>
        </main>

        <!-- ── Settings Drawer ── -->
        <div x-show="settingsOpen" @click="settingsOpen = false" x-cloak x-transition.opacity class="fixed inset-0 top-14 bg-black/55 backdrop-blur-sm z-30"></div>
        <aside :class="settingsOpen ? 'translate-x-0' : 'translate-x-full'"
               class="fixed top-14 right-0 bottom-0 w-80 max-w-[90vw] bg-[var(--color-surface)] border-l border-[var(--color-border)] shadow-[-10px_0_40px_-10px_rgba(0,0,0,0.5)] transition-transform duration-300 z-40 overflow-y-auto scrollbar-thin">
          <div class="p-4 space-y-4">
            <div class="flex items-center justify-between border-b border-[var(--color-border)] pb-2.5">
              <span class="text-[11px] uppercase tracking-wider text-[var(--color-mute)] font-semibold" x-text="$store.ui.t('configuration')"></span>
              <button type="button" @click="settingsOpen = false" class="text-[var(--color-mute)] hover:text-red-500 text-xl font-bold cursor-pointer">×</button>
            </div>
            <div class="space-y-3">
              <div>
                <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('customUrl')"></label>
                <input x-model="customUrl" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none transition" placeholder="http://172.16.1.123:8001" />
              </div>
              <div>
                <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('modelName')"></label>
                <input x-model="customModel" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none transition" placeholder="gemma4-26b" />
              </div>
              <div>
                <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('apiKey')"></label>
                <input x-model="apiKey" type="password" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none transition" />
              </div>
              <button type="button" @click="connectCustom()" class="w-full bg-[var(--color-accent)] hover:bg-[var(--color-accent-hi)] text-white py-2 rounded-md text-sm font-medium transition cursor-pointer" x-text="$store.ui.t('connect')"></button>
            </div>
            <div class="text-[11px] uppercase tracking-wider text-[var(--color-mute)] border-b border-[var(--color-border)] pb-1.5 font-semibold" x-text="$store.ui.t('parameters')"></div>
            <div class="grid grid-cols-2 gap-2">
              <div>
                <div class="flex items-center justify-between mb-1">
                  <label class="text-[11px] text-[var(--color-mute)] uppercase tracking-wider" x-text="$store.ui.t('maxTokens')"></label>
                  <button type="button" @click="maxTokens = 32768" class="text-[9px] text-[var(--color-accent-hi)] hover:underline cursor-pointer">MAX</button>
                </div>
                <input x-model.number="maxTokens" type="number" min="1" max="32768" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none transition" />
              </div>
              <div>
                <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('temperature')"></label>
                <input x-model.number="temperature" type="number" min="0" max="2" step="0.1" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none transition" />
              </div>
            </div>
            <div>
              <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('stream')"></label>
              <select x-model="streamMode" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none transition">
                <option value="true" x-text="$store.ui.t('enabled')"></option>
                <option value="false" x-text="$store.ui.t('disabled')"></option>
              </select>
            </div>
            <div>
              <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('sysPrompt')"></label>
              <textarea x-model="systemPrompt" :placeholder="$store.ui.t('sysPromptPh')" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none min-h-16 resize-y transition"></textarea>
            </div>
            <div class="text-[11px] uppercase tracking-wider text-[var(--color-mute)] border-b border-[var(--color-border)] pb-1.5 font-semibold" x-text="$store.ui.t('agenticMode')"></div>
            <div class="flex items-center justify-between">
              <div>
                <div class="text-sm text-[var(--color-text)]" x-text="$store.ui.t('enableTools')"></div>
                <div class="text-[10px] text-[var(--color-mute-2)]" x-text="$store.ui.t('toolsBuiltin')"></div>
              </div>
              <button type="button" @click="agenticEnabled = !agenticEnabled"
                      :class="agenticEnabled ? 'bg-[var(--color-accent)]' : 'bg-[var(--color-surface-hi)]'"
                      class="relative w-10 h-5 rounded-full transition cursor-pointer shrink-0">
                <div :class="agenticEnabled ? 'translate-x-5' : 'translate-x-0.5'" class="absolute top-0.5 w-4 h-4 bg-white rounded-full shadow transition-transform"></div>
              </button>
            </div>
            <template x-if="agenticEnabled">
              <div class="space-y-3">
                <div>
                  <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('maxLoops')"></label>
                  <input x-model.number="maxAgentLoops" type="number" min="1" max="20" class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-sm outline-none transition" />
                </div>
                <div>
                  <label class="block text-[11px] text-[var(--color-mute)] uppercase tracking-wider mb-1" x-text="$store.ui.t('customTools')"></label>
                  <textarea x-model="customToolsJson"
                            class="w-full bg-[var(--color-bg)] border border-[var(--color-border)] focus:border-[var(--color-accent)] rounded-md px-2.5 py-1.5 text-xs font-mono outline-none min-h-24 resize-y transition"
                            placeholder='[{"type":"function","function":{"name":"my_tool","description":"..."}}]'></textarea>
                  <div class="text-[10px] text-[var(--color-mute-2)] mt-1" x-text="$store.ui.t('customToolsNote')"></div>
                </div>
              </div>
            </template>
            <div class="flex items-center justify-between py-1">
              <div>
                <div class="text-sm text-[var(--color-text)]" x-text="$store.ui.t('autoComplete')"></div>
                <div class="text-[10px] text-[var(--color-mute-2)]" x-text="$store.ui.t('autoCompleteDesc')"></div>
              </div>
              <button type="button" @click="autoComplete = !autoComplete"
                      :class="autoComplete ? 'bg-[var(--color-accent)]' : 'bg-[var(--color-surface-hi)]'"
                      class="relative w-10 h-5 rounded-full transition cursor-pointer shrink-0">
                <div :class="autoComplete ? 'translate-x-5' : 'translate-x-0.5'" class="absolute top-0.5 w-4 h-4 bg-white rounded-full shadow transition-transform"></div>
              </button>
            </div>
            <button type="button" @click="clearChat()" class="w-full bg-transparent border border-red-500/70 text-red-400 hover:bg-red-500/10 py-2 rounded-md text-sm font-medium transition cursor-pointer" x-text="$store.ui.t('clearChat')"></button>
          </div>
        </aside>
      </div>
    </div>

    <script>
      const SERVER_ENDPOINTS = <asp:Literal ID="EndpointsJsonLiteral" runat="server" />;

      // ── TR/EN string table ────────────────────────────────────────────────
      const STRINGS = {
        tr: {
          newChat:'Yeni Sohbet', search:'Konusma ara...', settings:'Ayarlar', logout:'Cikis',
          mode:'Mod', agentic:'Agentic', welcome:'Hosgeldiniz',
          noConvs:'Henuz konusma yok.', noMatch:'Eslesen yok', conversations:'konusma',
          dblRename:'Yeniden adlandirmak icin cift tiklayin',
          selectEp:'Endpoint secin veya Ayarlar\'i acin', thinking:'Dusunuyor',
          send:'Gonder', stop:'Durdur', regen:'Yeniden Uret',
          copy:'Kopyala', copied:'Kopyalandi!', copyFail:'Hata', txt:'TXT', code:'Kod',
          contBtn:'Devam Et', truncTitle:'Yanit kesildi',
          truncDesc:'Max token limitine ulasildi',
          avModes:'Kullanilabilir Modlar', noSkills:'Skill dosyasi bulunamadi.',
          noSkillsSub:'IIS/Skills/ klasorune .md ekleyin', refresh:'Yenile',
          inputPh:'Mesaj yazin... (Enter gonder, Shift+Enter yeni satir)',
          configuration:'Yapilandirma', customUrl:'Ozel Base URL',
          modelName:'Model Adi', apiKey:'API Anahtari', connect:'Baglan',
          parameters:'Parametreler', maxTokens:'Max Token', temperature:'Sicaklik',
          stream:'Akis', enabled:'Aktif', disabled:'Pasif',
          sysPrompt:'Sistem Promptu', sysPromptPh:'Istege bagli sistem promptu...',
          agenticMode:'Agentic Mod', enableTools:'Araclari Etkinlestir',
          toolsBuiltin:'tarih/saat - hesaplama - http_get - http_post',
          maxLoops:'Max Dongu', customTools:'Ozel Araclar (JSON)',
          customToolsNote:'Yerlesik araclara ek. JSON dizisi.',
          autoComplete:'Otomatik Tamamlama', autoCompleteDesc:'Kesilince otomatik devam eder',
          clearChat:'Sohbeti Temizle',
          connecting:'baglaniyor...', connected:'bagli',
          unreachable:'erisilemez', disconnected:'baglanti kesildi', notConnected:'bagli degil',
          arguments:'Parametreler', result:'Sonuc', executing:'calisiyor...', done:'tamamlandi',
          dark:'Karanlik', light:'Aydinlik', lang:'EN',
        },
        en: {
          newChat:'New Chat', search:'Search conversations...', settings:'Settings', logout:'Logout',
          mode:'Mode', agentic:'Agentic', welcome:'Welcome',
          noConvs:'No saved conversations yet.', noMatch:'No matches', conversations:'conversations',
          dblRename:'Double-click to rename',
          selectEp:'Select an endpoint or open Settings', thinking:'Thinking',
          send:'Send', stop:'Stop', regen:'Regenerate',
          copy:'Copy', copied:'Copied!', copyFail:'Error', txt:'TXT', code:'Code',
          contBtn:'Continue', truncTitle:'Response truncated',
          truncDesc:'Max token limit reached',
          avModes:'Available Modes', noSkills:'No skill files found.',
          noSkillsSub:'Add .md files to IIS/Skills/', refresh:'Refresh',
          inputPh:'Type a message... (Enter to send, Shift+Enter for newline)',
          configuration:'Configuration', customUrl:'Custom Base URL',
          modelName:'Model Name', apiKey:'API Key', connect:'Connect',
          parameters:'Parameters', maxTokens:'Max Tokens', temperature:'Temperature',
          stream:'Stream', enabled:'Enabled', disabled:'Disabled',
          sysPrompt:'System Prompt', sysPromptPh:'Optional system prompt...',
          agenticMode:'Agentic Mode', enableTools:'Enable Tools',
          toolsBuiltin:'datetime - calculate - http_get - http_post',
          maxLoops:'Max Loops', customTools:'Custom Tools (JSON)',
          customToolsNote:'Added on top of built-in tools. JSON array.',
          autoComplete:'Auto-complete', autoCompleteDesc:'Auto-continues when truncated',
          clearChat:'Clear Current Chat',
          connecting:'connecting...', connected:'connected',
          unreachable:'unreachable', disconnected:'disconnected', notConnected:'not connected',
          arguments:'Arguments', result:'Result', executing:'executing...', done:'done',
          dark:'Dark', light:'Light', lang:'TR',
        }
      };

      // ── Alpine store: lang + darkMode (shared across all components) ──────
      document.addEventListener('alpine:init', () => {
        const savedLang  = localStorage.getItem('setllm-lang')  || 'tr';
        const savedTheme = localStorage.getItem('setllm-theme') || 'dark';
        Alpine.store('ui', {
          lang: savedLang,
          darkMode: savedTheme === 'dark',
          t(k) { return (STRINGS[this.lang] || STRINGS.tr)[k] || k; },
          toggleLang() {
            this.lang = this.lang === 'tr' ? 'en' : 'tr';
            localStorage.setItem('setllm-lang', this.lang);
          },
          toggleTheme() {
            this.darkMode = !this.darkMode;
            const th = this.darkMode ? 'dark' : 'light';
            document.documentElement.setAttribute('data-theme', th);
            localStorage.setItem('setllm-theme', th);
            const el = document.getElementById('hljs-theme');
            if (el) el.href = 'https://cdn.jsdelivr.net/npm/@highlightjs/cdn-assets@11/styles/'
              + (this.darkMode ? 'github-dark' : 'github') + '.min.css';
          }
        });
        // Apply saved hljs theme if light
        if (savedTheme === 'light') {
          const el = document.getElementById('hljs-theme');
          if (el) el.href = 'https://cdn.jsdelivr.net/npm/@highlightjs/cdn-assets@11/styles/github.min.css';
        }
      });

      // ── Code block copy (http + https fallback) ───────────────────────────
      window.copyCode = async (btn) => {
        const code = btn.closest('.code-block-wrapper').querySelector('code').innerText;
        const lang    = window.Alpine?.store?.('ui')?.lang || 'tr';
        const copied  = STRINGS[lang]?.copied  || 'Copied!';
        const copyLbl = STRINGS[lang]?.copy    || 'Copy';
        let ok = false;
        try { await navigator.clipboard.writeText(code); ok = true; } catch {}
        if (!ok) {
          const ta = Object.assign(document.createElement('textarea'), { value: code });
          Object.assign(ta.style, { position:'fixed', opacity:'0', top:'0', left:'0' });
          document.body.appendChild(ta); ta.focus(); ta.select();
          try { document.execCommand('copy'); ok = true; } catch {}
          document.body.removeChild(ta);
        }
        btn.textContent = ok ? copied : (STRINGS[lang]?.copyFail || 'Error');
        setTimeout(() => btn.textContent = copyLbl, 2000);
      };

      // ── Built-in tool schemas sent with every agentic request ──
      const BUILTIN_TOOL_SCHEMAS = [
        { type: 'function', function: { name: 'get_datetime', description: 'Returns current date, time and timezone.', parameters: { type: 'object', properties: {} } } },
        { type: 'function', function: { name: 'calculate', description: 'Evaluates a math expression (JS Math available).', parameters: { type: 'object', properties: { expression: { type: 'string', description: 'e.g. "Math.sqrt(144)" or "2**10"' } }, required: ['expression'] } } },
        { type: 'function', function: { name: 'http_get', description: 'HTTP GET via server-side proxy. Use for internal APIs.', parameters: { type: 'object', properties: { url: { type: 'string' } }, required: ['url'] } } },
        { type: 'function', function: { name: 'http_post', description: 'HTTP POST via server-side proxy. Use for internal APIs.', parameters: { type: 'object', properties: { url: { type: 'string' }, body: { type: 'object', description: 'JSON body' } }, required: ['url', 'body'] } } },
      ];

      // ── Configure marked ──
      (function() {
        if (typeof marked === 'undefined') return;
        marked.use({
          gfm: true, breaks: true,
          renderer: {
            code: function(tok, lang2) {
              const text = typeof tok === 'object' ? (tok.text || '') : (tok || '');
              const lang = typeof tok === 'object' ? (tok.lang || '') : (lang2 || '');
              let hl;
              try {
                hl = lang && typeof hljs !== 'undefined' && hljs.getLanguage(lang)
                  ? hljs.highlight(text, { language: lang, ignoreIllegals: true }).value
                  : typeof hljs !== 'undefined' ? hljs.highlightAuto(text).value
                    : text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
              } catch { hl = text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
              const _copyLbl = (window.Alpine?.store?.('ui')?.lang === 'tr' ? STRINGS.tr : STRINGS.en).copy || 'Copy';
              return `<div class="code-block-wrapper"><div class="code-lang-bar"><span>${lang||'text'}</span><button class="copy-btn" onclick="window.copyCode(this)">${_copyLbl}</button></div><pre><code class="hljs">${hl}</code></pre></div>`;
            }
          }
        });
      })();

      function setLlmApp() {
        const STORAGE_KEY = 'set-llm-conversations';

        return {
          // ── Global ──────────────────────────────────────────────────────
          endpoints: (() => {
            const host = window.location.hostname && window.location.hostname !== 'localhost' ? window.location.hostname : '172.16.0.160';
            return (SERVER_ENDPOINTS || []).map(ep => ({ ...ep, host: ep.host || host }));
          })(),
          activeEpIdx: null, activeBaseUrl: null, activeModel: null,
          status: 'not connected', statusOk: null,
          customUrl: '', customModel: '',
          apiKey: '9cbef01e3edede04363783bbe87ed51fc178aa0a3586697cde4aa915ebcdaecf',
          maxTokens: 1024, temperature: 0.7, streamMode: 'true', systemPrompt: '',
          agenticEnabled: false, maxAgentLoops: 10, customToolsJson: '[]',
          autoComplete: false,
          input: '', attachedImage: null, settingsOpen: false, historyOpen: true, searchQuery: '',
          skillsOpen: false, skills: [], activeSkillId: null, activeSkillName: '',
          conversations: [], currentConvId: null,

          // ── Derived ──────────────────────────────────────────────────────
          get currentConv() { return this.conversations.find(c => c.id === this.currentConvId) || null; },
          get messages()    { return this.currentConv?.messages   || []; },
          get generating()  { return this.currentConv?.generating || false; },
          get stats()       { return this.currentConv?.stats      || null; },
          get sortedConversations() {
            const q = (this.searchQuery || '').toLowerCase().trim();
            let list = q ? this.conversations.filter(c =>
              (c.title || '').toLowerCase().includes(q) ||
              (c.messages || []).some(m => (m.content || '').toLowerCase().includes(q))
            ) : this.conversations;
            return list.slice().sort((a, b) => (b.updatedAt || 0) - (a.updatedAt || 0));
          },

          // ── Init ────────────────────────────────────────────────────────
          init() { this.loadConversations(); this.autoConnect(); this._startHealthPing(); this.loadSkills(); },

          // ── Conversation management ─────────────────────────────────────
          _makeConv(id) {
            return {
              id, title: 'New Chat', createdAt: Date.now(), updatedAt: Date.now(),
              messages: [], apiHistory: [], generating: false, abortController: null, stats: null, totalTokens: 0,
              model: this.activeModel || null, baseUrl: this.activeBaseUrl || null, endpointIdx: this.activeEpIdx,
              systemPrompt: this.systemPrompt || '', temperature: this.temperature ?? 0.7,
              maxTokens: this.maxTokens || 1024, streamMode: this.streamMode || 'true',
              agenticEnabled: this.agenticEnabled, maxAgentLoops: this.maxAgentLoops || 10,
              customTools: [], autoComplete: this.autoComplete,
              skillId: this.activeSkillId || null, skillName: this.activeSkillName || '',
            };
          },

          _snapshotSettings(convId) {
            const c = this.conversations.find(x => x.id === convId); if (!c) return;
            c.systemPrompt = this.systemPrompt; c.temperature = this.temperature;
            c.maxTokens = this.maxTokens;       c.streamMode = this.streamMode;
            c.model = this.activeModel || c.model; c.baseUrl = this.activeBaseUrl || c.baseUrl;
            c.endpointIdx = this.activeEpIdx;
            c.agenticEnabled = this.agenticEnabled; c.maxAgentLoops = this.maxAgentLoops;
            c.autoComplete = this.autoComplete;
            try { c.customTools = JSON.parse(this.customToolsJson || '[]'); } catch { c.customTools = []; }
            c.skillId = this.activeSkillId || null; c.skillName = this.activeSkillName || '';
          },

          _restoreSettings(conv) {
            if (!conv) return;
            this.systemPrompt = conv.systemPrompt ?? ''; this.temperature  = conv.temperature  ?? 0.7;
            this.maxTokens    = conv.maxTokens    ?? 1024; this.streamMode = conv.streamMode   ?? 'true';
            this.agenticEnabled = conv.agenticEnabled ?? false; this.maxAgentLoops = conv.maxAgentLoops ?? 10;
            this.autoComplete = conv.autoComplete ?? false;
            this.customToolsJson = (conv.customTools?.length) ? JSON.stringify(conv.customTools, null, 2) : '[]';
            this.activeSkillId   = conv.skillId   || null;
            this.activeSkillName = conv.skillName || '';
            if (conv.baseUrl) { this.activeBaseUrl = conv.baseUrl; this.customUrl   = conv.baseUrl; }
            if (conv.model)   { this.activeModel   = conv.model;   this.customModel = conv.model; }
            if (conv.endpointIdx != null) this.activeEpIdx = conv.endpointIdx;
          },

          loadConversations() {
            try {
              const raw = localStorage.getItem(STORAGE_KEY);
              if (raw) this.conversations = JSON.parse(raw).map(c => ({
                ...c, generating: false, abortController: null, stats: null,
                totalTokens: c.totalTokens || 0, customTools: c.customTools || [],
              }));
            } catch { this.conversations = []; }
          },

          persistConversations() {
            const save = this.conversations.map(c => ({
              id: c.id, title: c.title, createdAt: c.createdAt, updatedAt: c.updatedAt,
              messages: (c.messages || []).map(m => { const { _buf, _inThink, streaming, ...r } = m; return { ...r, streaming: false }; }),
              apiHistory: c.apiHistory, totalTokens: c.totalTokens || 0,
              model: c.model, baseUrl: c.baseUrl, endpointIdx: c.endpointIdx,
              systemPrompt: c.systemPrompt, temperature: c.temperature, maxTokens: c.maxTokens, streamMode: c.streamMode,
              agenticEnabled: c.agenticEnabled || false, maxAgentLoops: c.maxAgentLoops || 10,
              customTools: c.customTools || [], autoComplete: c.autoComplete || false,
              skillId: c.skillId || null, skillName: c.skillName || '',
            }));
            try { localStorage.setItem(STORAGE_KEY, JSON.stringify(save)); }
            catch { while (save.length > 1) { save.pop(); this.conversations.pop(); try { localStorage.setItem(STORAGE_KEY, JSON.stringify(save)); return; } catch {} } }
          },

          newConversation() {
            if (this.currentConvId) this._snapshotSettings(this.currentConvId);
            const id = 'c_' + Date.now() + '_' + Math.random().toString(36).slice(2, 8);
            this.conversations.unshift(this._makeConv(id));
            this.currentConvId = id; this.input = ''; this.clearImage();
          },

          loadConversation(id) {
            if (this.currentConvId === id) return;
            if (this.currentConvId) this._snapshotSettings(this.currentConvId);
            this.currentConvId = id;
            this._restoreSettings(this.currentConv);
            this.input = ''; this.clearImage();
            this.$nextTick(() => this.scrollMessages());
          },

          deleteConversation(id) {
            const c = this.conversations.find(x => x.id === id);
            if (c?.abortController) c.abortController.abort();
            this.conversations = this.conversations.filter(x => x.id !== id);
            if (this.currentConvId === id) {
              const next = this.sortedConversations[0];
              this.currentConvId = next?.id || null;
              if (this.currentConvId) this._restoreSettings(this.currentConv);
              else { this.input = ''; this.clearImage(); }
            }
            this.persistConversations();
          },

          renameConversation(id, title) {
            const c = this.conversations.find(x => x.id === id);
            if (c) { c.title = (title || '').trim() || 'Untitled'; this.persistConversations(); }
          },

          _updateConvTitle(conv) {
            if (conv.title && conv.title !== 'New Chat') return;
            const u = conv.messages.find(m => m.role === 'user'); if (!u) return;
            const src = typeof u.content === 'string' && u.content ? u.content : (u.image ? 'Image conversation' : 'Untitled');
            conv.title = src.slice(0, 48).trim();
          },

          saveConversation(convId) {
            const c = this.conversations.find(x => x.id === convId); if (!c) return;
            this._updateConvTitle(c); c.updatedAt = Date.now();
            this._snapshotSettings(convId); this.persistConversations();
          },

          formatDate(ts) {
            if (!ts) return '';
            const d = new Date(ts), t = new Date();
            return d.toDateString() === t.toDateString()
              ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
              : d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
          },
          formatTokenCount(n) { return !n ? '0' : n >= 1000 ? (n/1000).toFixed(1)+'k' : String(n); },

          // ── Connection ───────────────────────────────────────────────────
          async checkHealth(url) {
            try { const r = await fetch(url + '/health', { signal: AbortSignal.timeout(3000) }); return r.ok || r.status === 401 || r.status === 403; }
            catch { return false; }
          },
          async detectModel(url) {
            try { const r = await fetch(url + '/v1/models', { headers: { Authorization: 'Bearer ' + this.apiKey }, signal: AbortSignal.timeout(4000) }); return (await r.json()).data?.[0]?.id || null; }
            catch { return null; }
          },
          async autoConnect() {
            for (let i = 0; i < this.endpoints.length; i++) {
              const ep = this.endpoints[i];
              if (await this.checkHealth(`http://${ep.host}:${ep.port}`)) { await this.connectEndpoint(i); return; }
            }
          },
          async connectEndpoint(idx) { this.activeEpIdx = idx; const ep = this.endpoints[idx]; await this.connect(`http://${ep.host}:${ep.port}`, ep.model); },
          async connectCustom() { const url = (this.customUrl||'').trim().replace(/\/$/,''); if (!url) return; this.activeEpIdx = null; await this.connect(url, (this.customModel||'').trim()||null); },
          async connect(url, hint) {
            this.status = 'connecting…'; this.statusOk = null;
            const ok = await this.checkHealth(url);
            if (!ok) { this.status = 'unreachable'; this.statusOk = false; return; }
            const model = hint || await this.detectModel(url) || 'unknown';
            this.activeBaseUrl = url; this.activeModel = model;
            this.customUrl = url; this.customModel = model;
            this.status = 'connected'; this.statusOk = true;
          },
          _startHealthPing() {
            setInterval(async () => {
              if (!this.activeBaseUrl || this.statusOk === null) return;
              const ok = await this.checkHealth(this.activeBaseUrl);
              if (!ok && this.statusOk)  { this.status = 'disconnected'; this.statusOk = false; }
              if (ok  && !this.statusOk) { await this.connect(this.activeBaseUrl, this.activeModel); }
            }, 30000);
          },

          // ── Skills ────────────────────────────────────────────────────────
          async loadSkills() {
            try {
              const r = await fetch(window.location.href, { headers: { 'X-List-Skills': 'true' } });
              if (r.ok) this.skills = await r.json();
            } catch {}
          },

          async activateSkill(skill) {
            try {
              const r = await fetch(window.location.href, { headers: { 'X-Load-Skill': skill.id } });
              if (!r.ok) return;
              const content = await r.text();
              // Apply to current conversation (or create new one if none)
              if (!this.currentConv) this.newConversation();
              this.systemPrompt = content;
              this.activeSkillId   = skill.id;
              this.activeSkillName = skill.name;
              // Snapshot into current conv immediately
              if (this.currentConvId) this._snapshotSettings(this.currentConvId);
            } catch {}
            this.skillsOpen = false;
          },

          deactivateSkill() {
            this.activeSkillId = null; this.activeSkillName = '';
            this.systemPrompt = '';
            if (this.currentConvId) this._snapshotSettings(this.currentConvId);
            this.skillsOpen = false;
          },

          // ── Image ────────────────────────────────────────────────────────
          onImageSelect(e) {
            const f = e.target.files[0]; if (!f) return;
            const r = new FileReader(); r.onload = ev => { this.attachedImage = { base64: ev.target.result, name: f.name }; }; r.readAsDataURL(f);
          },
          onPaste(e) {
            for (const item of (e.clipboardData?.items || [])) {
              if (item.type.startsWith('image/')) {
                e.preventDefault();
                const r = new FileReader(); r.onload = ev => { this.attachedImage = { base64: ev.target.result, name: 'pasted-image.png' }; }; r.readAsDataURL(item.getAsFile()); break;
              }
            }
          },
          clearImage() { this.attachedImage = null; if (this.$refs.fileInput) this.$refs.fileInput.value = ''; },
          autoResize(ta) { ta.style.height = 'auto'; ta.style.height = Math.min(ta.scrollHeight, 160) + 'px'; },

          clearChat() {
            const c = this.currentConv; if (!c) return;
            if (c.abortController) c.abortController.abort();
            Object.assign(c, { messages: [], apiHistory: [], generating: false, abortController: null, stats: null, totalTokens: 0, title: 'New Chat' });
            this.input = ''; this.clearImage(); this.persistConversations();
          },
          scrollMessages() { const el = document.getElementById('messages-scroll'); if (el) el.scrollTop = el.scrollHeight; },
          stopGeneration() { this.currentConv?.abortController?.abort(); },

          // ── Rendering ────────────────────────────────────────────────────
          renderContent(msg) {
            const text = msg.content || '';
            if (msg.streaming) return text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;') + '<span class="cursor-blink" style="color:var(--color-accent)"></span>';
            if (msg.role === 'user') return text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\n/g,'<br>');
            if (!text) return '';
            try { return marked.parse(text); } catch { return text.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
          },

          async copyMessage(idx, event) {
            const m = this.messages[idx]; if (!m) return;
            const t = m.thinking ? `[Thinking]\n${m.thinking}\n\n[Response]\n${m.content||''}` : (m.content||'');
            let ok = false;
            try { await navigator.clipboard.writeText(t); ok = true; } catch {}
            if (!ok) {
              // HTTP fallback (clipboard API requires HTTPS)
              const ta = Object.assign(document.createElement('textarea'), { value: t });
              Object.assign(ta.style, { position:'fixed', opacity:'0', top:'0', left:'0' });
              document.body.appendChild(ta); ta.focus(); ta.select();
              try { document.execCommand('copy'); ok = true; } catch {}
              document.body.removeChild(ta);
            }
            // Visual feedback on the button
            if (event?.currentTarget) {
              const btn = event.currentTarget;
              const orig = btn.innerHTML;
              const _s = Alpine?.store?.('ui'); const _l = _s?.lang || 'tr';
              btn.innerHTML = '<svg class="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" stroke-linejoin="round" d="M5 13l4 4L19 7"/></svg> ' + (ok ? (STRINGS[_l]?.copied||'Copied!') : (STRINGS[_l]?.copyFail||'Error'));
              setTimeout(() => btn.innerHTML = orig, 2000);
            }
          },

          hasCode(m) {
            return /```[\s\S]*?```/.test(m?.content || '');
          },

          downloadCode(idx) {
            const m = this.messages[idx]; if (!m) return;
            const blocks = [];
            const re = /```(\w*)\n?([\s\S]*?)```/g;
            let match, i = 1;
            while ((match = re.exec(m.content || '')) !== null) {
              const lang = match[1] || 'text';
              const code = match[2].trim();
              if (code) { blocks.push(`// === Block ${i} (${lang}) ===\n${code}`); i++; }
            }
            if (!blocks.length) return;
            const a = Object.assign(document.createElement('a'), {
              href: URL.createObjectURL(new Blob([blocks.join('\n\n')], { type:'text/plain;charset=utf-8' })),
              download: `code_${Date.now()}.txt`,
            });
            document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(a.href);
          },
          downloadMessage(idx) {
            const m = this.messages[idx]; if (!m) return;
            const t = m.thinking ? `[Thinking]\n${m.thinking}\n\n[Response]\n${m.content||''}` : (m.content||'');
            const a = Object.assign(document.createElement('a'), { href: URL.createObjectURL(new Blob([t], {type:'text/plain;charset=utf-8'})), download: `response_${Date.now()}.txt` });
            document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(a.href);
          },

          // ── Error helpers ────────────────────────────────────────────────
          async logError(err, ctx) {
            try { await fetch(window.location.href, { method:'POST', headers:{'X-Log-Error':'true','Content-Type':'text/plain'}, body:'MODEL: '+(this.activeModel||'None')+'\nINPUT: '+(ctx||'')+'\nERROR: '+err }); } catch {}
          },
          errorMessageFor(e) { return (e.includes('LiteLLM Virtual Key expected')||e.includes('auth_error')) ? 'Please wait while model warming up' : 'Error: '+e; },

          // ── Token parser ─────────────────────────────────────────────────
          processToken(convId, aIdx, token) {
            const conv = this.conversations.find(c => c.id === convId); if (!conv) return;
            const msg = conv.messages[aIdx]; if (!msg) return;
            msg._buf = (msg._buf||'') + token;
            while (true) {
              if (!msg._inThink) {
                const i = msg._buf.indexOf('<think>');
                if (i === -1) { msg.content += msg._buf; msg._buf = ''; break; }
                msg.content += msg._buf.slice(0, i); msg._buf = msg._buf.slice(i+7); msg._inThink = true;
              } else {
                const i = msg._buf.indexOf('</think>');
                if (i === -1) { msg.thinking += msg._buf; msg._buf = ''; break; }
                msg.thinking += msg._buf.slice(0, i); msg._buf = msg._buf.slice(i+8); msg._inThink = false;
              }
            }
            if (this.currentConvId === convId) this.scrollMessages();
          },

          finalize(convId, aIdx, t0, tokens, ttft, finishReason = null) {
            const conv = this.conversations.find(c => c.id === convId); if (!conv) return;
            const elapsed = (performance.now()-t0)/1000;
            conv.stats = { ttft: ttft!=null?(ttft/1000).toFixed(2)+'s':'-', tps: tokens>0?(tokens/elapsed).toFixed(1):'-', tokens, elapsed: elapsed.toFixed(2)+'s' };
            conv.totalTokens = (conv.totalTokens||0) + (tokens||0);
            const m = conv.messages[aIdx];
            if (m) { m.streaming = false; m.truncated = finishReason === 'length'; m.meta = new Date().toLocaleTimeString(); delete m._buf; delete m._inThink; }
            conv.generating = false; conv.abortController = null;
          },

          // ── Core fetch loop ──────────────────────────────────────────────
          async _runGeneration(convId, aIdx, logContext) {
            const getConv = () => this.conversations.find(c => c.id === convId);
            const conv = getConv(); if (!conv) return null;

            const sysPr = conv.systemPrompt || '';
            const apiMessages = [...(sysPr.trim() ? [{role:'system',content:sysPr.trim()}] : []), ...conv.apiHistory];
            const tools = conv.agenticEnabled ? [...BUILTIN_TOOL_SCHEMAS, ...(conv.customTools||[])] : [];
            const body = {
              model: conv.model || this.activeModel,
              messages: apiMessages,
              max_tokens: conv.maxTokens || 1024,
              temperature: conv.temperature ?? 0.7,
              stream: (conv.streamMode ?? 'true') === 'true',
              ...(tools.length ? { tools, tool_choice: 'auto' } : {}),
            };
            const url = (conv.baseUrl || this.activeBaseUrl) + '/v1/chat/completions';

            conv.generating = true; conv.stats = null;
            conv.abortController = new AbortController();
            const t0 = performance.now();
            let ttft = null, tokenCount = 0, finishReason = null, errorOccurred = false;
            let toolCallsAcc = {}, toolCalls = [];
            let finalResult = null;

            try {
              const resp = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + this.apiKey },
                body: JSON.stringify(body),
                signal: conv.abortController.signal,
              });

              if (body.stream) {
                if (!resp.ok) {
                  errorOccurred = true;
                  const err = await resp.text(); await this.logError(err, logContext);
                  const c = getConv(); if (c) c.messages[aIdx].content = this.errorMessageFor(err);
                } else {
                  const reader = resp.body.getReader(); const dec = new TextDecoder(); let buf = '';
                  while (true) {
                    const c = getConv();
                    if (!c || c.abortController?.signal.aborted) { reader.cancel(); break; }
                    const { done, value } = await reader.read(); if (done) break;
                    buf += dec.decode(value, { stream: true });
                    const lines = buf.split('\n'); buf = lines.pop() || '';
                    for (const line of lines) {
                      if (getConv()?.abortController?.signal.aborted) break;
                      if (!line.startsWith('data: ')) continue;
                      const data = line.slice(6).trim(); if (data === '[DONE]') continue;
                      try {
                        const j = JSON.parse(data);
                        const fr = j.choices?.[0]?.finish_reason; if (fr) finishReason = fr;
                        // Accumulate tool_call deltas
                        const tcDs = j.choices?.[0]?.delta?.tool_calls;
                        if (tcDs) {
                          for (const tc of tcDs) {
                            const i = tc.index || 0;
                            if (!toolCallsAcc[i]) toolCallsAcc[i] = { id:'', name:'', argsStr:'' };
                            if (tc.id)                toolCallsAcc[i].id       += tc.id;
                            if (tc.function?.name)    toolCallsAcc[i].name     += tc.function.name;
                            if (tc.function?.arguments) toolCallsAcc[i].argsStr += tc.function.arguments;
                          }
                        }
                        const delta = j.choices?.[0]?.delta?.content || '';
                        if (!delta) continue;
                        if (ttft === null) ttft = performance.now() - t0;
                        tokenCount++;
                        this.processToken(convId, aIdx, delta);
                      } catch {}
                    }
                  }
                  // Parse accumulated tool calls
                  toolCalls = Object.values(toolCallsAcc).map(tc => ({
                    id: tc.id, name: tc.name,
                    args: (() => { try { return JSON.parse(tc.argsStr); } catch { return {}; } })(),
                  }));
                  const c = getConv(); if (c && toolCalls.length) c.messages[aIdx].toolCalls = toolCalls;
                }
              } else {
                const j = await resp.json();
                if (j.error) {
                  errorOccurred = true;
                  const err = JSON.stringify(j.error); await this.logError(err, logContext);
                  const c = getConv(); if (c) c.messages[aIdx].content = this.errorMessageFor(err);
                } else {
                  const content = j.choices?.[0]?.message?.content || '';
                  finishReason = j.choices?.[0]?.finish_reason || null;
                  ttft = performance.now() - t0;
                  tokenCount = j.usage?.completion_tokens || content.split(/\s+/).length;
                  // Non-streaming tool_calls
                  const rawTc = j.choices?.[0]?.message?.tool_calls;
                  if (rawTc?.length) {
                    toolCalls = rawTc.map(tc => ({ id: tc.id, name: tc.function?.name, args: (() => { try { return JSON.parse(tc.function?.arguments||'{}'); } catch { return {}; } })() }));
                    finishReason = 'tool_calls';
                  }
                  const c = getConv();
                  if (c) {
                    if (toolCalls.length) { c.messages[aIdx].toolCalls = toolCalls; }
                    else {
                      const tm = content.match(/^([\s\S]*?)<think>([\s\S]*?)<\/think>([\s\S]*)$/);
                      if (tm) { c.messages[aIdx].thinking = tm[2].trim(); c.messages[aIdx].content = (tm[1]+tm[3]).trim(); }
                      else c.messages[aIdx].content = content;
                    }
                  }
                }
              }
            } catch (e) {
              if (e.name !== 'AbortError') {
                errorOccurred = true;
                await this.logError(e.message||String(e), logContext);
                const c = getConv(); if (c) c.messages[aIdx].content = 'Error: '+(e.message||String(e));
              }
            } finally {
              const c = getConv();
              const wasAborted = c?.abortController?.signal.aborted;
              this.finalize(convId, aIdx, t0, tokenCount, ttft, finishReason === 'tool_calls' ? null : finishReason);
              finalResult = { c, wasAborted, errorOccurred, toolCalls };
            }
            return finalResult;
          },

          // ── Agentic loop ─────────────────────────────────────────────────
          async _runAgenticLoop(convId, aIdx, logContext) {
            const getConv = () => this.conversations.find(c => c.id === convId);
            let conv = getConv(); if (!conv) return null;
            const maxLoops = conv.maxAgentLoops || 10;

            let result = await this._runGeneration(convId, aIdx, logContext);
            let currentAIdx = aIdx;

            for (let loop = 0; loop < maxLoops; loop++) {
              if (!result?.toolCalls?.length || result.wasAborted || result.errorOccurred) break;
              conv = result.c; if (!conv) break;

              const assistantMsg = conv.messages[currentAIdx];

              // Push assistant message with tool_calls to apiHistory
              conv.apiHistory.push({
                role: 'assistant',
                content: assistantMsg?.content || null,
                tool_calls: result.toolCalls.map(tc => ({ id: tc.id, type: 'function', function: { name: tc.name, arguments: JSON.stringify(tc.args) } })),
              });

              // Execute each tool call
              for (const tc of result.toolCalls) {
                const toolIdx = conv.messages.length;
                conv.messages.push({ role: 'tool_call', toolName: tc.name, args: tc.args, result: null, meta: new Date().toLocaleTimeString() });
                if (this.currentConvId === convId) this.scrollMessages();

                const toolResult = await this.executeToolCall(tc.name, tc.args);
                conv.messages[toolIdx].result = toolResult;

                conv.apiHistory.push({ role: 'tool', tool_call_id: tc.id, content: typeof toolResult === 'string' ? toolResult : JSON.stringify(toolResult) });
              }

              // New assistant placeholder
              currentAIdx = conv.messages.length;
              conv.messages.push({ role: 'assistant', content: '', thinking: '', meta: '', streaming: true, _buf: '', _inThink: false, toolCalls: [] });
              if (this.currentConvId === convId) this.scrollMessages();

              result = await this._runGeneration(convId, currentAIdx, logContext);
            }

            // Final: push assistant response to apiHistory
            if (result && !result.wasAborted && !result.errorOccurred && result.c && !result.toolCalls?.length) {
              const final = result.c.messages[currentAIdx]?.content || result.c.messages[currentAIdx]?.thinking || '';
              if (final) result.c.apiHistory.push({ role: 'assistant', content: final });
            }
            return result;
          },

          // ── Tool execution ───────────────────────────────────────────────
          async executeToolCall(name, args) {
            const a = args || {};
            try {
              if (name === 'get_datetime') {
                const now = new Date();
                return { iso: now.toISOString(), local: now.toLocaleString(), unix: Math.floor(now/1000), timezone: Intl.DateTimeFormat().resolvedOptions().timeZone };
              }
              if (name === 'calculate') {
                if (!a.expression) return { error: 'expression is required' };
                const result = Function('"use strict"; const Math=globalThis.Math; return (' + a.expression + ')')();
                return { result, expression: a.expression };
              }
              if (name === 'http_get')  return await this._proxyRequest('GET',  a.url, null, null);
              if (name === 'http_post') return await this._proxyRequest('POST', a.url, a.body, a.content_type);
              return { error: 'Unknown tool: ' + name };
            } catch (e) { return { error: e.message || String(e) }; }
          },

          async _proxyRequest(method, url, body, contentType) {
            if (!url) return { error: 'url is required' };
            try {
              const resp = await fetch(window.location.href, {
                method: 'POST',
                headers: { 'X-Tool-Proxy': 'true', 'Content-Type': 'application/json' },
                body: JSON.stringify({ method, url, body: body ? JSON.stringify(body) : null, contentType: contentType || 'application/json' }),
              });
              const text = await resp.text();
              if (!resp.ok) return { error: `HTTP ${resp.status}: ${text}` };
              try { return JSON.parse(text); } catch { return text; }
            } catch (e) { return { error: e.message || String(e) }; }
          },

          // ── Send / Regenerate / Continue ─────────────────────────────────
          async sendMessage() {
            if (!this.activeBaseUrl) return;
            if (!this.currentConv) this.newConversation();
            const conv = this.currentConv;
            if (conv.generating) return;
            const text = (this.input || '').trim();
            if (!text && !this.attachedImage) return;

            this.input = '';
            if (this.$refs.textarea) this.$refs.textarea.style.height = 'auto';

            const convId = conv.id;
            let userApiContent, userImage = null;
            if (this.attachedImage) {
              userApiContent = [{ type:'text', text: text||'Analyze this image' }, { type:'image_url', image_url:{ url: this.attachedImage.base64 } }];
              userImage = this.attachedImage.base64;
            } else { userApiContent = text; }

            conv.messages.push({ role:'user', content:text, image:userImage, thinking:'', meta:new Date().toLocaleTimeString(), streaming:false });
            conv.apiHistory.push({ role:'user', content:userApiContent });
            this.clearImage();
            this.$nextTick(() => this.scrollMessages());

            this._snapshotSettings(convId);
            const aIdx = conv.messages.length;
            conv.messages.push({ role:'assistant', content:'', thinking:'', meta:'', streaming:true, _buf:'', _inThink:false, toolCalls:[] });

            await this._runAgenticLoop(convId, aIdx, text);
            if (this.conversations.find(c => c.id === convId)?.autoComplete)
              await this._autoCompleteLoop(convId);
            this.saveConversation(convId);
            if (this.currentConvId === convId) this.$nextTick(() => this.scrollMessages());
          },

          async regenerate() {
            const conv = this.currentConv; if (!conv || conv.generating) return;
            const lastAsstIdx = conv.messages.map(m => m.role).lastIndexOf('assistant'); if (lastAsstIdx === -1) return;
            conv.messages.splice(lastAsstIdx, 1);
            const lastAsstApiIdx = conv.apiHistory.map(m => m.role).lastIndexOf('assistant');
            if (lastAsstApiIdx >= 0) conv.apiHistory.splice(lastAsstApiIdx, 1);
            const convId = conv.id; this._snapshotSettings(convId);
            const aIdx = conv.messages.length;
            conv.messages.push({ role:'assistant', content:'', thinking:'', meta:'', streaming:true, _buf:'', _inThink:false, toolCalls:[] });
            await this._runAgenticLoop(convId, aIdx, '');
            if (this.conversations.find(c => c.id === convId)?.autoComplete)
              await this._autoCompleteLoop(convId);
            this.saveConversation(convId);
            if (this.currentConvId === convId) this.$nextTick(() => this.scrollMessages());
          },

          async continueResponse(aIdx) {
            const conv = this.currentConv; if (!conv || conv.generating) return;
            const msg = conv.messages[aIdx]; if (!msg || msg.role !== 'assistant' || !msg.truncated) return;
            const convId = conv.id;
            await this._doContinue(convId, aIdx);
            // If autoComplete is on, keep going until model stops on its own
            if (this.conversations.find(c => c.id === convId)?.autoComplete)
              await this._autoCompleteLoop(convId);
            this.saveConversation(convId);
            if (this.currentConvId === convId) this.$nextTick(() => this.scrollMessages());
          },

          // ── Auto-complete loop ────────────────────────────────────────────
          // Runs when autoComplete is on: keeps continuing until finish_reason = 'stop'
          // Works even when the user has switched to a different conversation.
          async _autoCompleteLoop(convId) {
            let safety = 50; // hard cap — prevents runaway
            while (safety-- > 0) {
              const conv = this.conversations.find(c => c.id === convId);
              if (!conv || !conv.autoComplete || conv.generating) break;
              const lastAsstIdx = conv.messages.map(m => m.role).lastIndexOf('assistant');
              if (lastAsstIdx === -1) break;
              if (!conv.messages[lastAsstIdx]?.truncated) break;
              await this._doContinue(convId, lastAsstIdx);
            }
          },

          // Shared single-step continuation (used by both continueResponse and _autoCompleteLoop)
          async _doContinue(convId, aIdx) {
            const conv = this.conversations.find(c => c.id === convId); if (!conv) return;
            const msg = conv.messages[aIdx]; if (!msg) return;
            msg.truncated = false; msg.streaming = true; msg._buf = ''; msg._inThink = false;
            conv.apiHistory.push({ role:'user', content:'Continue' });
            const result = await this._runGeneration(convId, aIdx, '');
            if (result?.c) {
              const ci = result.c.apiHistory.map(m => m.role).lastIndexOf('user');
              if (ci >= 0 && result.c.apiHistory[ci].content === 'Continue') result.c.apiHistory.splice(ci, 1);
              if (!result.wasAborted && !result.errorOccurred) {
                const ai = result.c.apiHistory.map(m => m.role).lastIndexOf('assistant');
                const full = result.c.messages[aIdx]?.content || '';
                if (ai >= 0) result.c.apiHistory[ai].content = full;
                else if (full) result.c.apiHistory.push({ role:'assistant', content:full });
              }
            }
          },
        };
      }
    </script>
  </asp:Panel>

</form>
</body>
</html>
