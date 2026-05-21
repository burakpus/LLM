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
  { id: 'admin',   icon: '⚙️', label: 'Admin Paneli'   },
  { id: 'tips',    icon: '💡', label: 'İpuçları'       },
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
      <UL([
        '☰  Sol panel — sohbet listesi, favoriler, arşiv, proje',
        'Orta alan — mesajlaşma alanı',
        'Üst bar — mod, model adı, ayarlar, tema, dil',
        'Alt bar — mesaj yazma, resim ekleme, mod seçici pill\'ler',
        'Sağ panel — proje modunda dosya paneli',
      ])} />
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
        <UL(['Belge analizi ve özetleme', 'Görsel anlama (resim gönderebilirsiniz)', 'Yazı düzenleme ve çeviri']) />
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
        <UL(['SQL sorgu yazımı ve optimizasyonu', 'Python, C#, JavaScript ve tüm diller', 'Kod inceleme ve hata düzeltme', 'Otonom Mod ile hesaplama, HTTP istekleri']) />
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
        <UL(['PDF, DOCX, TXT, MD dosyalarında arama', 'Skill seçilince ilgili koleksiyonda arama', 'Yanıtta kaç döküman referans alındığı gösterilir']) />
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
      <UL([
        'Fatura, makbuz, belge okuma',
        'Diyagram ve grafik analizi',
        'Barkod ve etiket içeriği okuma',
        'Ekran görüntüsü açıklama',
        'Görsel karşılaştırma',
      ])} />

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
      <UL([
        'Dosya sekmesine tıkla → içerik görünür',
        '✏️ Düzenle → textarea açılır',
        'Değişiklikler yeşil/kırmızı diff olarak gösterilir',
        'Tamam → sunucuya kaydedilir | Reddet → iptal',
      ])} />

      <H>Tab Tıklama → Chat Context</H>
      <p className="text-sm" style={{ color: 'var(--text-2)' }}>
        Dosya sekmesine tıklayınca dosya adı otomatik olarak chat input'una eklenir
        (<Code>@rapor.sql</Code>). Üstüne yazarak hızla soru sorabilirsiniz.
      </p>

      <H>Proje-Sohbet İlişkisi</H>
      <UL([
        'Her sohbet bir projeye bağlanabilir',
        'Sohbet değişince proje paneli otomatik güncellenir',
        'Aynı projeye birden fazla sohbet bağlanabilir',
        'Sidebar\'da proje bağlı sohbetlerde 📁 ikonu görünür',
      ])} />

      <Tip>Dosyalar DGX sunucusunda ~/llm-projects/{kullanıcı}/{proje}/ klasöründe saklanır.</Tip>
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

      {[
        { tab: 'Upload', icon: '📤', items: [
          'Collection adını girin (örn: RMGenelge, default)',
          'Dosyaları sürükle-bırak veya browse ile seçin',
          'Upload & ingest — chunk sayısı gösterilir',
          'Desteklenen: PDF, DOCX, TXT, MD',
        ]},
        { tab: 'Documents', icon: '📄', items: [
          'Yüklü dökümanlar kaynak ve koleksiyona göre listelenir',
          'Collection dropdown ile filtreleme',
          'Delete → Confirm ile döküman silinir',
        ]},
        { tab: 'Skills', icon: '🎯', items: [
          'Mevcut skill .md dosyaları listelenir',
          '+ Skill Yükle (.md) ile yeni skill eklenebilir',
          'Listeden seçince içerik sağda görünür',
          'Hover → çöp kutusu ile silinebilir',
        ]},
        { tab: 'Kullanım', icon: '📊', items: [
          'Kullanıcı bazlı token tablosu (prompt + completion)',
          'Model bazlı kullanım (chat / code)',
          'Son 50 istek detayı',
        ]},
      ].map(s => (
        <div key={s.tab} className="p-3 rounded-lg space-y-1" style={{ background: 'var(--surface-2)', border: '1px solid var(--border)' }}>
          <div className="flex items-center gap-2 font-medium text-sm mb-2" style={{ color: 'var(--text)' }}>
            <span>{s.icon}</span> {s.tab}
          </div>
          <UL(s.items)} />
        </div>
      ))}
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
  start:   ContentStart,
  modes:   ContentModes,
  skills:  ContentSkills,
  image:   ContentImage,
  agent:   ContentAgent,
  project: ContentProject,
  fav:     ContentFav,
  admin:   ContentAdmin,
  tips:    ContentTips,
}

// ── Main modal ────────────────────────────────────────────────────────────────
export default function HelpModal({ onClose }: { onClose: () => void }) {
  const [active, setActive] = useState('start')
  const ActiveContent = CONTENT[active] ?? ContentStart

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
          </nav>

          {/* Content */}
          <main className="flex-1 overflow-y-auto p-6">
            <h2 className="text-xl font-semibold mb-4" style={{ color: 'var(--text)' }}>
              {SECTIONS.find(s => s.id === active)?.icon}{' '}
              {SECTIONS.find(s => s.id === active)?.label}
            </h2>
            <ActiveContent />
          </main>
        </div>
      </div>
    </div>
  )
}
