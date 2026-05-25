import { useState } from 'react'

// ── Section types ─────────────────────────────────────────────────────────────
interface Section {
  id:    string
  icon:  string
  label: string
}

const SECTIONS: Section[] = [
  { id: 'start',   icon: '🚀', label: 'Başlarken'      },
  { id: 'modes',   icon: '💬', label: 'Sohbet Modları' },
  { id: 'skills',  icon: '🎯', label: 'Skill Sistemi'  },
  { id: 'image',   icon: '🖼️', label: 'Resim Gönderme' },
  { id: 'agent',   icon: '🤖', label: 'Otonom Mod'     },
  { id: 'project', icon: '📁', label: 'Proje Modu'     },
  { id: 'fav',     icon: '⭐', label: 'Favoriler & Arşiv' },
  { id: 'tips',    icon: '💡', label: 'İpuçları'       },
]

const ADMIN_SECTIONS: Section[] = [
  { id: 'admin',      icon: '⚙️', label: 'Admin Paneli'     },
  { id: 'admin-sql',  icon: '🗄️', label: 'SQL Kaynakları'    },
  { id: 'admin-jobs', icon: '⚡', label: 'Arka Plan İşleri' },
]

// ── Small components ──────────────────────────────────────────────────────────
function Badge({ children, color = 'var(--accent)' }: { children: string; color?: string }) {
  return (
    <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-semibold"
          style={{ background: `${color}22`, color, border: `1px solid ${color}44` }}>
      {children}
    </span>
  )
}

function Step({ n, children }: { n: number; children: string }) {
  return (
    <div className="flex gap-3 items-start">
      <span className="shrink-0 w-5 h-5 rounded-full flex items-center justify-center text-[11px] font-bold mt-0.5"
            style={{ background: 'var(--accent)', color: '#0b1929' }}>{n}</span>
      <span className="text-sm leading-relaxed" style={{ color: 'var(--text)' }}>{children}</span>
    </div>
  )
}

function Tip({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex gap-2 p-3 rounded-lg text-sm"
         style={{ background: 'rgba(138,180,248,0.08)', border: '1px solid rgba(138,180,248,0.2)' }}>
      <span className="shrink-0">💡</span>
      <span style={{ color: 'var(--text-2)' }}>{children}</span>
    </div>
  )
}

function Warn({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex gap-2 p-3 rounded-lg text-sm"
         style={{ background: 'rgba(251,191,36,0.08)', border: '1px solid rgba(251,191,36,0.25)' }}>
      <span className="shrink-0">⚠️</span>
      <span style={{ color: 'var(--text-2)' }}>{children}</span>
    </div>
  )
}

function Code({ children }: { children: string }) {
  return (
    <code className="px-1.5 py-0.5 rounded text-[12px] font-mono"
          style={{ background: 'var(--surface-hi)', color: '#a78bfa' }}>
      {children}
    </code>
  )
}

function H({ children }: { children: string }) {
  return <h3 className="font-semibold text-base mb-2 mt-4" style={{ color: 'var(--text)' }}>{children}</h3>
}

function UL({ items }: { items: string[] }) {
  return (
    <ul className="space-y-1.5 my-2">
      {items.map((it, i) => (
        <li key={i} className="flex gap-2 text-sm" style={{ color: 'var(--text-2)' }}>
          <span className="shrink-0 mt-1.5 w-1 h-1 rounded-full" style={{ background: 'var(--mute)' }} />
          {it}
        </li>
      ))}
    </ul>
  )
}

// ── Content sections ──────────────────────────────────────────────────────────
function ContentStart() {
  return (
    <div className="space-y-4">
      <p className="text-sm leading-relaxed" style={{ color: 'var(--text-2)' }}>
        SET LLM, şirket içi yapay zeka platformudur. Tüm veriler DGX Spark sunucusunda
        işlenir, dışarıya çıkmaz.
      </p>
      <div className="p-3 rounded-lg" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
        <div className="text-xs font-semibold mb-1" style={{ color: 'var(--mute)' }}>ADRES</div>
        <Code>http://172.16.1.123:5080</Code>
      </div>
      <H>Giriş Yapma</H>
      <div className="space-y-2">
        <Step n={1}>Kullanıcı adınızı girin (AD/LDAP kullanıcı adınız)</Step>
        <Step n={2}>Şifrenizi girin</Step>
        <Step n={3}>Domain seçin: SETYAZILIM veya SETSOFTWARE</Step>
        <Step n={4}>Giriş Yap'a tıklayın — 8 saat geçerli oturum açılır</Step>
      </div>
      <H>Ekran Düzeni</H>
      <UL items={[
        '☰  Sol panel — sohbet listesi, favoriler, arşiv, proje',
        'Orta alan — mesajlaşma alanı',
        'Üst bar — mod, model adı, ayarlar, tema, dil',
        'Alt bar — mesaj yazma, resim ekleme, mod seçici pill\'ler',
        'Sağ panel — proje modunda dosya paneli',
      ]} />
    </div>
  )
}

function ContentModes() {
  return (
    <div className="space-y-5">
      {/* Chating */}
      <div className="p-4 rounded-xl space-y-2" style={{ border: '1px solid var(--border)', background: 'var(--surface-2)' }}>
        <div className="flex items-center gap-2">
          <span className="text-lg">💬</span>
          <span className="font-semibold" style={{ color: 'var(--text)' }}>Chating</span>
          <Badge>Gemma 4 26B</Badge>
        </div>
        <p className="text-sm" style={{ color: 'var(--text-2)' }}>
          Genel amaçlı sohbet. Türkçe ve İngilizce tam destek, görsel anlama, uzun metin analizi.
        </p>
        <UL items={['Belge analizi ve özetleme', 'Görsel anlama (resim gönderebilirsiniz)', 'Yazı düzenleme ve çeviri']} />
      </div>

      {/* Coding */}
      <div className="p-4 rounded-xl space-y-2" style={{ border: '1px solid var(--border)', background: 'var(--surface-2)' }}>
        <div className="flex items-center gap-2">
          <span className="text-lg">🖥️</span>
          <span className="font-semibold" style={{ color: 'var(--text)' }}>Coding</span>
          <Badge color="#34a853">Qwen3 27B</Badge>
        </div>
        <p className="text-sm" style={{ color: 'var(--text-2)' }}>
          Kod yazma ve analiz için optimize edilmiş mod. Otonom Mod ile araç çağırabilir.
        </p>
        <UL items={['SQL sorgu yazımı ve optimizasyonu', 'Python, C#, JavaScript ve tüm diller', 'Kod inceleme ve hata düzeltme', 'Otonom Mod ile hesaplama, HTTP istekleri']} />
      </div>

      {/* RAG */}
      <div className="p-4 rounded-xl space-y-2" style={{ border: '1px solid var(--border)', background: 'var(--surface-2)' }}>
        <div className="flex items-center gap-2">
          <span className="text-lg">🔍</span>
          <span className="font-semibold" style={{ color: 'var(--text)' }}>RAG</span>
          <Badge color="#f59e0b">Bilgi Tabanı</Badge>
        </div>
        <p className="text-sm" style={{ color: 'var(--text-2)' }}>
          Admin panelinden yüklenen şirket içi dökümanlardan bilgi çekme modu.
        </p>
        <UL items={['PDF, DOCX, TXT, MD dosyalarında arama', 'Skill seçilince ilgili koleksiyonda arama', 'Yanıtta kaç döküman referans alındığı gösterilir']} />
      </div>

      <Tip>Alt bardaki pill'lere tıklayarak modlar arasında geçiş yapabilirsiniz. Her sohbet kendi modunu hatırlar.</Tip>
    </div>
  )
}

function ContentSkills() {
  return (
    <div className="space-y-4">
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Skilllar, belirli görevler için hazırlanmış sistem promptlarıdır. Skill seçilince AI o konuda uzmanlaşır; RAG modunda yalnızca ilgili döküman koleksiyonunda arama yapar.
      </p>

      <H>Skill Seçmek İçin</H>
      <div className="space-y-2">
        <Step n={1}>Üst bardaki Mod butonuna tıklayın</Step>
        <Step n={2}>Açılan listeden skill'i seçin</Step>
        <Step n={3}>Seçilen skill header'da görünür — × ile kapatılabilir</Step>
      </div>

      <H>Mevcut Skilllar</H>
      <div className="space-y-2">
        {[
          { name: 'CFS DB Model Assistant', desc: 'CFS veritabanı modeli için SQL asistanı. Tablo/kolon açıklamalarını biliyor.' },
          { name: 'Excel Asistanı', desc: 'Excel formül, makro ve Power Query desteği.' },
          { name: 'Analiz Asistanı', desc: 'İş analizi, raporlama ve veri yorumlama.' },
          { name: 'RMGenelge', desc: 'Risk Yönetimi yönetmelik asistanı. Yalnızca RMGenelge dökümanlarında arama yapar.' },
        ].map(s => (
          <div key={s.name} className="p-3 rounded-lg" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <div className="font-medium text-sm mb-0.5" style={{ color: 'var(--text)' }}>{s.name}</div>
            <div className="text-xs" style={{ color: 'var(--mute)' }}>{s.desc}</div>
          </div>
        ))}
      </div>

      <Tip>Yeni skill eklemek için Admin paneli → Skills → + Skill Yükle (.md)</Tip>
    </div>
  )
}

function ContentImage() {
  return (
    <div className="space-y-4">
      <Warn>Resim gönderme yalnızca Chating modunda çalışır. Coding veya RAG modunda 📎 butonu görünmez.</Warn>

      <H>Resim Gönderme</H>
      <div className="space-y-2">
        <Step n={1}>📎 butonuna tıklayın VEYA görseli Ctrl+V ile yapıştırın</Step>
        <Step n={2}>Mesaj alanının üstünde resim önizlemesi görünür</Step>
        <Step n={3}>İsteğe bağlı mesaj yazın ve gönderin</Step>
        <Step n={4}>Model resmi okuyup analiz eder</Step>
      </div>

      <H>Ne Yapabilirsiniz?</H>
      <UL items={[
        'Fatura, makbuz, belge okuma',
        'Diyagram ve grafik analizi',
        'Barkod ve etiket içeriği okuma',
        'Ekran görüntüsü açıklama',
        'Görsel karşılaştırma',
      ]} />

      <div className="p-3 rounded-lg text-sm" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
        <span className="font-medium" style={{ color: 'var(--text)' }}>Desteklenen formatlar: </span>
        <span style={{ color: 'var(--text-2)' }}>JPG, PNG, GIF, WebP</span>
        <br />
        <span className="font-medium" style={{ color: 'var(--text)' }}>Maksimum boyut: </span>
        <span style={{ color: 'var(--text-2)' }}>768×768 px (otomatik yeniden boyutlandırılır)</span>
      </div>
    </div>
  )
}

function ContentAgent() {
  return (
    <div className="space-y-4">
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Otonom Mod, modelin birden fazla adımda araç kullanarak görevi tamamlamasını sağlar.
        Coding modunda kullanılabilir.
      </p>

      <H>Etkinleştirme</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Ayarlar paneli (⚙️) → <span className="font-medium" style={{ color: 'var(--text)' }}>Otonom</span> checkbox'ını işaretleyin.
      </p>

      <H>Yerleşik Araçlar</H>
      <div className="space-y-2">
        {[
          { tool: 'get_datetime', desc: 'Güncel tarih, saat ve zaman dilimi' },
          { tool: 'calculate',   desc: 'JavaScript math ifadesi hesaplama (Math.sqrt, Math.PI vb.)' },
          { tool: 'http_get',    desc: 'Sunucu üzerinden GET isteği' },
          { tool: 'http_post',   desc: 'Sunucu üzerinden POST isteği' },
        ].map(t => (
          <div key={t.tool} className="flex items-start gap-3 p-3 rounded-lg"
               style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <Code>{t.tool}</Code>
            <span className="text-sm" style={{ color: 'var(--text-2)' }}>{t.desc}</span>
          </div>
        ))}
      </div>

      <H>RAG Agent Modu</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Alt bardaki <span className="font-medium" style={{ color: 'var(--text)' }}>RAG</span> pill'i
        tam bir ajan pipeline açar: vektör arama + BM25 hibrit, oturum hafızası, skill sistem promptu.
        Otonom Mod'dan farklı olarak backend'de yönetilir.
      </p>

      <Tip>Otonom Mod açıkken resim gönderirseniz tools otomatik devre dışı kalır — görsel anlama ve araç çağırma aynı anda çalışmaz.</Tip>
    </div>
  )
}

function ContentProject() {
  return (
    <div className="space-y-4">
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Proje Modu, AI ile birlikte dosya oluşturmanızı ve düzenlemenizi sağlar.
        SQL sorguları, kod dosyaları ve her türlü metin dosyası projeye kaydedilebilir.
      </p>

      <H>Proje Oluşturma</H>
      <div className="space-y-2">
        <Step n={1}>Sol panelde + Yeni Proje butonuna tıklayın</Step>
        <Step n={2}>Proje adını girin (örn: credit-report, crm-api)</Step>
        <Step n={3}>Enter — sağda proje paneli açılır</Step>
      </div>

      <H>Dosya Oluşturma — 3 Yöntem</H>
      <div className="space-y-3">
        <div className="p-3 rounded-lg space-y-1" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <div className="flex items-center gap-2 font-medium text-sm" style={{ color: 'var(--text)' }}>
            <span>📁</span> AI Kod Bloğundan
          </div>
          <p className="text-xs" style={{ color: 'var(--text-2)' }}>
            AI bir kod/SQL yazdığında bloğun sağ üstünde 📁 ikonu belirir.
            Tıklayın → dosya adı girin → Enter.
          </p>
        </div>
        <div className="p-3 rounded-lg space-y-1" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <div className="flex items-center gap-2 font-medium text-sm" style={{ color: 'var(--text)' }}>
            <span>✏️</span> Elle Oluşturma
          </div>
          <p className="text-xs" style={{ color: 'var(--text-2)' }}>
            Proje panelinde + Yeni → dosya adı ve içerik gir → Kaydet.
          </p>
        </div>
        <div className="p-3 rounded-lg space-y-1" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <div className="flex items-center gap-2 font-medium text-sm" style={{ color: 'var(--text)' }}>
            <span>🤖</span> AI Dosya Formatı
          </div>
          <p className="text-xs" style={{ color: 'var(--text-2)' }}>
            AI'ya <Code>"bunu rapor.sql dosyası olarak kaydet"</Code> deyin.
            AI <Code>file:rapor.sql</Code> formatında yanıt verir, otomatik eklenir.
          </p>
        </div>
      </div>

      <H>Düzenleme & Diff</H>
      <UL items={[
        'Dosya sekmesine tıkla → içerik görünür',
        '✏️ Düzenle → textarea açılır',
        'Değişiklikler yeşil/kırmızı diff olarak gösterilir',
        'Tamam → sunucuya kaydedilir | Reddet → iptal',
      ]} />

      <H>Tab Tıklama → Chat Context</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Dosya sekmesine tıklayınca dosya adı otomatik olarak chat input'una eklenir
        (<Code>@rapor.sql</Code>). Üstüne yazarak hızla soru sorabilirsiniz.
      </p>

      <H>Proje-Sohbet İlişkisi</H>
      <UL items={[
        'Her sohbet bir projeye bağlanabilir',
        'Sohbet değişince proje paneli otomatik güncellenir',
        'Aynı projeye birden fazla sohbet bağlanabilir',
        'Sidebar\'da proje bağlı sohbetlerde 📁 ikonu görünür',
      ]} />

      <Tip>Dosyalar DGX sunucusunda ~/llm-projects/&#123;kullanıcı&#125;/&#123;proje&#125;/ klasöründe saklanır.</Tip>
    </div>
  )
}

function ContentFav() {
  return (
    <div className="space-y-4">
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Sidebar'ı temiz tutmak için sohbetleri favorileyin veya arşivleyin.
      </p>

      <H>Favoriler ⭐</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Sık kullandığınız sohbetleri favorilere ekleyin — sidebar'ın en üstünde ayrı bölümde görünür.
      </p>
      <div className="space-y-2">
        <Step n={1}>Sohbet satırının üzerine gelin</Step>
        <Step n={2}>⭐ ikonuna tıklayın</Step>
        <Step n={3}>★ Favoriler bölümüne taşınır</Step>
      </div>

      <H>Arşiv 📦</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Eski veya tamamlanmış sohbetleri arşivleyin — görünmez olur ama silinmez.
      </p>
      <div className="space-y-2">
        <Step n={1}>Sohbet satırının üzerine gelin</Step>
        <Step n={2}>📦 ikonuna tıklayın</Step>
        <Step n={3}>Arşivlenen sohbet sayısı sidebar altında görünür</Step>
        <Step n={4}>📦 Arşiv (n) bölümüne tıklayarak erişebilirsiniz</Step>
      </div>

      <Tip>Sohbet adını değiştirmek için başlığa çift tıklayın.</Tip>
    </div>
  )
}

function ContentAdmin() {
  return (
    <div className="space-y-4">
      <div className="p-3 rounded-lg" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
        <div className="text-xs font-semibold mb-1" style={{ color: 'var(--mute)' }}>ADMIN PANELİ</div>
        <Code>http://172.16.1.123:5080/admin</Code>
      </div>

      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Admin panelinde 9 sekme bulunur. SQL Kaynakları ve İşler için ayrı yardım bölümleri var (sol menüde).
      </p>

      {[
        { tab: 'Upload', icon: '📤', items: [
          'Collection adını girin (örn: RMGenelge, default)',
          'Dosyaları sürükle-bırak veya browse ile seçin',
          'Upload & ingest — chunk sayısı gösterilir',
          'Desteklenen: PDF, DOCX, XLSX, TXT, MD',
        ]},
        { tab: 'Documents', icon: '📄', items: [
          'Yüklü dökümanlar kaynak ve koleksiyona göre listelenir',
          'Collection dropdown ile filtreleme',
          'Delete → Confirm ile döküman silinir (RAG\'dan da çıkar)',
        ]},
        { tab: 'Skills', icon: '🎯', items: [
          'Mevcut skill .md dosyaları VEYA klasör tabanlı skill\'ler listelenir',
          '+ Skill Yükle (.md/.zip) — tek dosya veya skill klasörü zip',
          '📥 Anthropic Import — anthropics/skills repo\'sundan 17 skill seçip indirme',
          'Listeden seçince içerik sağda önizlenir',
          'Folder skill\'ler 📁 N rozetiyle gösterilir (N = referans .md sayısı)',
          'Skill\'in name: alanı dropdown\'da gösterilir',
        ]},
        { tab: 'Şablonlar', icon: '📝', items: [
          'Prompt şablonları kütüphanesi (slash command picker için)',
          '+ Yeni → ad + içerik + koleksiyon alanları',
          'Chat\'te / yazıp şablon seçilebilir',
          'Few-shot örnekler skill başına ayrı yönetilir',
        ]},
        { tab: 'SQL', icon: '🗄️', items: [
          'MS SQL / PostgreSQL / MySQL / Oracle bağlantıları',
          'Detay: sol menüde "SQL Kaynakları" yardım bölümü',
        ]},
        { tab: 'İşler', icon: '⚡', items: [
          'Arka plan job kuyruğu — tip/durum filtresi, iptal, tekrar dene',
          'Detay: sol menüde "Arka Plan İşleri" yardım bölümü',
        ]},
        { tab: 'Kullanım', icon: '📊', items: [
          'Kullanıcı bazlı token tablosu (prompt + completion + maliyet)',
          'Model bazlı kullanım (chat / code)',
          'Son 50 istek detayı + 👍/👎 oy geri bildirimleri',
        ]},
        { tab: 'Aktivite', icon: '📋', items: [
          'Tüm yönetici işlemlerinin kronolojik kaydı (legacy)',
          'Döküman/skill/şablon/SQL bağlantı eylemleri',
          'Filtre: action tipi (örn: sql.connection.create)',
          'Her satır: kullanıcı + tarih + hedef + detay',
        ]},
        { tab: '🛡 Güvenlik', icon: '🛡', items: [
          'OWASP Logging Cheat Sheet uyumlu denetim kaydı (event_log)',
          'Kategoriler: Auth · Authz · Session · Input · Config · Data · Security · System',
          'Severity: Debug · Info · Warn · Error · Critical',
          'Her olay: zaman + kim + IP + User-Agent + Request ID + sebep + JSON detay',
          'Otomatik: login fail, 401/403, rate limit, config değişimi',
          'Filtre: kategori × severity × tip × kullanıcı × IP × sonuç + serbest arama',
          'Son 24 saat özeti — chip\'e tıkla → o filtreyle listele',
        ]},
        { tab: '⚙ Ayarlar', icon: '⚙️', items: [
          'Connection (LLM endpoint) ayarları',
          'Sistem promptu — chat sırasında ilave talimat',
          'Yalnızca admin görebilir/değiştirebilir',
        ]},
      ].map(s => (
        <div key={s.tab} className="p-3 rounded-lg space-y-1" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <div className="flex items-center gap-2 font-medium text-sm mb-2" style={{ color: 'var(--text)' }}>
            <span>{s.icon}</span> {s.tab}
          </div>
          <UL items={s.items} />
        </div>
      ))}
    </div>
  )
}

function ContentAdminSql() {
  return (
    <div className="space-y-4">
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        SQL Kaynakları sekmesi, harici veritabanlarındaki şemayı ve veriyi RAG&apos;a aktarmak için kullanılır.
        Şifreler sunucuda DataProtection ile şifrelenerek saklanır.
      </p>

      <H>Desteklenen Veritabanları</H>
      <UL items={[
        'Microsoft SQL Server (varsayılan port 1433)',
        'PostgreSQL (varsayılan port 5432)',
        'MySQL / MariaDB (varsayılan port 3306)',
        'Oracle (varsayılan port 1521, SERVICE_NAME kullanılır)',
      ]} />

      <H>1) Bağlantı Tanımlama</H>
      <div className="space-y-2">
        <Step n={1}>+ Yeni Bağlantı → tip seçin, host/port/database/kullanıcı/şifre girin</Step>
        <Step n={2}>🔌 Test ile bağlantıyı doğrulayın (rate limit: 10/dk/kullanıcı)</Step>
        <Step n={3}>Sorgu zaman aşımı: 5..3600 sn, varsayılan 120 sn</Step>
        <Step n={4}>Otomatik veri sync interval seçin (kapalı / 15dk / saatlik / günlük / haftalık)</Step>
      </div>

      <H>2) Şema Çıkarımı — 📜 Şema butonu</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Veritabanındaki tüm CREATE script&apos;lerini RAG&apos;a yazar (tablolar, view&apos;lar, procedure&apos;ler, function&apos;lar, trigger&apos;lar).
        Bir kez yapılır, sonra "Sync" ile artımlı güncellenir.
      </p>
      <UL items={[
        '📜 Şema → modal açılır, mevcut iş varsa "Şu an çalışıyor" gösterir',
        'Koleksiyon adı + dahil edilecek obje tipleri seçilir',
        '🚀 Arka Planda Çalıştır → job kuyruğa girer, modal kapatılabilir',
        'İlerleme: ayrı bir progress modal\'da ETA ile gösterilir',
      ]} />

      <H>3) Şema Sync — 🔄 Sync butonu</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        İlk çıkarım sonrası kullanılır. Değişen / yeni / silinen objeleri tespit edip günceller (hash karşılaştırması).
      </p>
      <UL items={[
        '🔄 Sync → son sync durumu + Yeni Senkron Başlat butonu',
        'Aktif sync varsa "İlerlemeyi izle" butonu görünür',
        'Sonuç: yeni / değişen / aynı / silinen sayıları',
      ]} />

      <H>4) Veri Senkronu — 💾 Veri butonu</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Tablo satırlarını RAG&apos;a aktarır. Satır-bazlı delta sync ile sadece değişen kayıtlar ingest edilir.
      </p>
      <UL items={[
        'Her tablo için PK kolonu zorunlu (composite için "col1,col2")',
        'Created/Updated tarih kolonları seçilir — delta için Updated gerekli',
        'WHERE filtresi (örn: aktif = 1 AND silinmis = 0)',
        'Satır limiti (default 1000, max 100000)',
        'Kolon seçimi: tüm kolonlar / sadece seçilenler (PII otomatik maskelenir)',
        'Tablolar gruplara atanabilir (📁 Krediler, Ödemeler, vs.)',
      ]} />

      <H>5) Toplu İşlemler</H>
      <UL items={[
        'Yapılandırılmış tabloların yanındaki checkbox ile seçim',
        'Grup başlığındaki checkbox → tüm grubu seç/bırak',
        'Seçim varken üstte "→ gruba ata" dropdown\'u açılır',
        'Tek tıkla seçilenler yeni gruba taşınır',
      ]} />

      <H>6) Otomatik Sync</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Bağlantı formundaki &quot;Otomatik Veri Sync&quot; preset&apos;i seçilirse, sistem dakikada bir kontrol eder ve süresi gelen bağlantılar için
        otomatik sync job&apos;u kuyruğa atar. Bağlantı listesinde <Code>⏱ Ndk</Code> rozeti gösterilir.
      </p>

      <H>7) Sync Geçmişi</H>
      <UL items={[
        'Her tablo için son sync durumu satır altında gösterilir',
        '✓ +N / ↻M → başarılı sync (eklenen / güncellenen sayısı)',
        '✕ son sync hata → tooltip\'te hata mesajı',
      ]} />

      <Warn>Mevcut SQL Server&apos;lar için TLS uyumluluğu otomatik (Encrypt=Optional). Eski sunucularda da çalışır.</Warn>
    </div>
  )
}

function ContentAdminJobs() {
  return (
    <div className="space-y-4">
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Tüm uzun süren işlemler (şema çıkarımı, sync, veri ingest, otomatik sync) arka plan job kuyruğu üzerinden yürütülür.
        Sunucu yeniden başlatılırsa "running" durumdaki işler otomatik olarak "queued" durumuna geri alınır.
      </p>

      <H>Job Tipleri</H>
      <div className="space-y-2">
        {[
          { type: 'sql.ingest-schema', desc: 'İlk şema çıkarımı — tüm CREATE script\'leri RAG\'a' },
          { type: 'sql.sync-schema',   desc: 'Şema artımlı sync — değişen/yeni/silinen objeler' },
          { type: 'sql.ingest-data',   desc: 'Tablo verisi örnekleme (eski API)' },
          { type: 'sql.sync-data',     desc: 'Satır-bazlı delta data sync' },
        ].map(t => (
          <div key={t.type} className="flex items-start gap-3 p-3 rounded-lg"
               style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <Code>{t.type}</Code>
            <span className="text-sm" style={{ color: 'var(--text-2)' }}>{t.desc}</span>
          </div>
        ))}
      </div>

      <H>İşler Sekmesi (Admin → İşler)</H>
      <UL items={[
        'Tip filtresi — sadece belirli job tipini göster',
        'Durum filtresi — Kuyrukta / Çalışıyor / Tamamlandı / Hata / İptal',
        'Çalışan veya kuyrukta iş varsa 4 saniyede bir otomatik yenilenir',
        'Sayfa başına 50 kayıt, sayfalama',
        'Süre, ilerleme yüzdesi, kullanıcı, mesaj sütunları',
      ]} />

      <H>İşlemler</H>
      <div className="space-y-2">
        <div className="flex items-start gap-3 p-3 rounded-lg"
             style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <span className="text-lg">🔍</span>
          <div>
            <div className="font-medium text-sm" style={{ color: 'var(--text)' }}>Detay</div>
            <div className="text-xs" style={{ color: 'var(--text-2)' }}>
              Job progress modal&apos;ını açar — canlı ilerleme, ETA, browser bildirimi.
            </div>
          </div>
        </div>
        <div className="flex items-start gap-3 p-3 rounded-lg"
             style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <span className="text-lg">🛑</span>
          <div>
            <div className="font-medium text-sm" style={{ color: 'var(--text)' }}>İptal</div>
            <div className="text-xs" style={{ color: 'var(--text-2)' }}>
              Yalnızca <Code>queued</Code> durumdaki işler iptal edilebilir. Çalışan job&apos;ları durdurmak için server&apos;ı yeniden başlatmak gerekir (sonra otomatik recovery devreye girer).
            </div>
          </div>
        </div>
        <div className="flex items-start gap-3 p-3 rounded-lg"
             style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <span className="text-lg">↻</span>
          <div>
            <div className="font-medium text-sm" style={{ color: 'var(--text)' }}>Tekrar Dene</div>
            <div className="text-xs" style={{ color: 'var(--text-2)' }}>
              <Code>failed</Code> veya <Code>cancelled</Code> job&apos;lar için. Aynı parametrelerle yeni bir job oluşturulur.
            </div>
          </div>
        </div>
      </div>

      <H>Eş Zamanlı İşçi (Concurrency)</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Varsayılan 2 paralel worker. <Code>appsettings.json → Jobs:Workers</Code> ile 1..8 arası ayarlanabilir.
        PostgreSQL&apos;in <Code>SKIP LOCKED</Code> mekanizması ile aynı işi iki worker&apos;ın almaması garantili.
      </p>

      <H>Otomatik Sync Zamanlayıcı</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        <Code>AutoSyncScheduler</Code> dakikada bir tarama yapar. Bir bağlantının <Code>auto_sync_interval_min</Code>
        süresi geçmişse <Code>sql.sync-data</Code> job&apos;u kuyruğa atılır. Halihazırda aktif bir sync varsa atlanır.
      </p>

      <Tip>Browser bildirimleri ilk kullanımda izin ister. İzin verilirse uzun süren job tamamlandığında masaüstü bildirimi gelir.</Tip>
    </div>
  )
}

function ContentTips() {
  return (
    <div className="space-y-4">
      <H>Klavye Kısayolları</H>
      <div className="rounded-lg overflow-hidden" style={{ border: '1px solid var(--border)' }}>
        {[
          ['Enter',         'Mesaj gönder'],
          ['Shift + Enter', 'Yeni satır'],
          ['Ctrl + V',      'Resim yapıştır (Chating modunda)'],
          ['Çift tıkla',    'Sohbet adını değiştir'],
        ].map(([k, v], i) => (
          <div key={i} className="flex items-center px-3 py-2 text-sm"
               style={{ borderBottom: i < 3 ? '1px solid var(--border)' : 'none',
                        background: i % 2 === 0 ? 'var(--surface-2)' : 'var(--surface)' }}>
            <Code>{k}</Code>
            <span className="ml-3" style={{ color: 'var(--text-2)' }}>{v}</span>
          </div>
        ))}
      </div>

      <H>Verimli Kullanım</H>
      <div className="space-y-2">
        {[
          'SQL için Coding modu + CFS DB Model Assistant skill\'ini kullanın',
          'Döküman sormak için RAG modu + ilgili skill\'i seçin',
          'Uzun sohbetlerde bağlam aşılıyorsa yeni sohbet açın',
          'Önemli sohbetleri ⭐ favorileyin',
          'Tamamlanan projeleri 📦 arşivleyin',
          'Proje modunda SQL sorgularını dosyaya kaydedin',
          'Tab\'a tıklayarak dosya bağlamını hızla chat\'e ekleyin',
        ].map((tip, i) => (
          <div key={i} className="flex gap-2 text-sm p-2.5 rounded-lg"
               style={{ background: 'var(--surface-2)', color: 'var(--text-2)' }}>
            <span className="text-green-400 shrink-0">✓</span> {tip}
          </div>
        ))}
      </div>

      <H>Sık Sorulan Sorular</H>
      <div className="space-y-3">
        {[
          { q: 'Verilerim dışarı çıkıyor mu?', a: 'Hayır. Tüm modeller şirket içi DGX Spark\'ta çalışır, internet kullanılmaz.' },
          { q: 'Sohbet geçmişim kayboldu mu?', a: 'Sohbetler tarayıcıda saklanır. Farklı tarayıcı veya cihazdan erişilemez.' },
          { q: '"Model henüz yükleniyor" ne anlama gelir?', a: 'Sunucu yeniden başlatılınca model yüklenmesi 5-10 dk sürer. Bekleyip tekrar deneyin.' },
          { q: 'Resim gönderemiyorum?', a: 'Resim gönderme sadece Chating modunda çalışır. Coding veya RAG modunda desteklenmez.' },
        ].map((faq, i) => (
          <div key={i} className="p-3 rounded-lg" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
            <div className="font-medium text-sm mb-1" style={{ color: 'var(--text)' }}>❓ {faq.q}</div>
            <div className="text-sm" style={{ color: 'var(--text-2)' }}>{faq.a}</div>
          </div>
        ))}
      </div>
    </div>
  )
}

const CONTENT: Record<string, React.FC> = {
  start:        ContentStart,
  modes:        ContentModes,
  skills:       ContentSkills,
  image:        ContentImage,
  agent:        ContentAgent,
  project:      ContentProject,
  fav:          ContentFav,
  admin:        ContentAdmin,
  'admin-sql':  ContentAdminSql,
  'admin-jobs': ContentAdminJobs,
  tips:         ContentTips,
}

// ── Main modal ────────────────────────────────────────────────────────────────
export default function HelpModal({ onClose, isAdmin }: { onClose: () => void; isAdmin?: boolean }) {
  const [active, setActive] = useState('start')
  const ActiveContent = CONTENT[active] ?? ContentStart
  const allSections = isAdmin ? [...SECTIONS, ...ADMIN_SECTIONS] : SECTIONS

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4"
         style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}
         onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="w-full max-w-3xl h-[85vh] flex flex-col rounded-2xl overflow-hidden shadow-2xl"
           style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}>

        {/* Header */}
        <div className="flex items-center gap-3 px-5 py-4 shrink-0"
             style={{ borderBottom: '1px solid var(--border)', background: 'var(--surface)' }}>
          <span className="text-2xl">📘</span>
          <div>
            <div className="font-semibold" style={{ color: 'var(--text)' }}>SET LLM — Yardım</div>
            <div className="text-xs" style={{ color: 'var(--mute)' }}>Kullanım kılavuzu</div>
          </div>
          <div className="flex-1" />
          <button onClick={onClose}
                  className="w-8 h-8 rounded-full flex items-center justify-center cursor-pointer transition text-lg"
                  style={{ color: 'var(--mute)' }}
                  onMouseEnter={e => (e.currentTarget as HTMLElement).style.background = 'var(--surface-hi)'}
                  onMouseLeave={e => (e.currentTarget as HTMLElement).style.background = 'transparent'}>
            ×
          </button>
        </div>

        <div className="flex flex-1 overflow-hidden">
          {/* Sidebar nav */}
          <nav className="w-44 shrink-0 overflow-y-auto py-2"
               style={{ borderRight: '1px solid var(--border)', background: 'var(--surface)' }}>
            {SECTIONS.map(s => (
              <button key={s.id}
                      onClick={() => setActive(s.id)}
                      className="w-full flex items-center gap-2.5 px-4 py-2.5 text-sm cursor-pointer transition text-left"
                      style={{
                        background: active === s.id ? 'var(--surface-hi)' : 'transparent',
                        color:      active === s.id ? 'var(--accent-hi)'  : 'var(--text-2)',
                        borderRight: active === s.id ? '2px solid var(--accent)' : '2px solid transparent',
                      }}>
                <span className="shrink-0">{s.icon}</span>
                <span>{s.label}</span>
              </button>
            ))}

            {isAdmin && (
              <>
                <div className="mx-3 my-2" style={{ borderTop: '1px solid var(--border)' }} />
                <div className="px-4 pb-1 text-[10px] uppercase tracking-wider font-semibold"
                     style={{ color: 'var(--mute)' }}>
                  Yönetici
                </div>
                {ADMIN_SECTIONS.map(s => (
                  <button key={s.id}
                          onClick={() => setActive(s.id)}
                          className="w-full flex items-center gap-2.5 px-4 py-2.5 text-sm cursor-pointer transition text-left"
                          style={{
                            background: active === s.id ? 'var(--surface-hi)' : 'transparent',
                            color:      active === s.id ? 'var(--accent-hi)'  : 'var(--text-2)',
                            borderRight: active === s.id ? '2px solid var(--accent)' : '2px solid transparent',
                          }}>
                    <span className="shrink-0">{s.icon}</span>
                    <span>{s.label}</span>
                  </button>
                ))}
              </>
            )}
          </nav>

          {/* Content */}
          <main className="flex-1 overflow-y-auto p-6">
            <h2 className="text-xl font-semibold mb-4" style={{ color: 'var(--text)' }}>
              {allSections.find(s => s.id === active)?.icon}{' '}
              {allSections.find(s => s.id === active)?.label}
            </h2>
            <ActiveContent />
          </main>
        </div>
      </div>
    </div>
  )
}
