const fs = require('fs'), yaml = require('js-yaml');
const KSC = require('kaitai-struct-compiler');
const ksy = yaml.load(fs.readFileSync('sg.ksy', 'utf8'));
const compiler = typeof KSC === 'function' ? new KSC() : (KSC.default ? new KSC.default() : KSC);
const run = (compiler.compile || KSC.compile).bind(compiler);
run('csharp', ksy, null, false).then(files => {
  fs.mkdirSync('out', { recursive: true });
  for (const [name, code] of Object.entries(files)) fs.writeFileSync('out/' + name, code);
  console.log('files:', Object.keys(files).join(', '));

  const outDir = '../../src/D2.Data/Sg';
  fs.mkdirSync(outDir, { recursive: true });
  // Кодировка строк — один экземпляр (Cp1251.cs): поиск по имени на каждое поле идёт под замком поставщика кодовых страниц.
  const generated = '#nullable disable\n' + files['Sg.cs'].split('System.Text.Encoding.GetEncoding("windows-1251")').join('Cp1251.Encoding');
  fs.writeFileSync(outDir + '/Sg.g.cs', generated);
  console.log('copied: out/Sg.cs -> ' + outDir + '/Sg.g.cs (+ #nullable disable, Cp1251.Encoding)');
}).catch(e => { console.error('ERR', e.message || e); process.exit(1); });
