(() => {
  const search = document.querySelector('.search');
  const input = search.querySelector('input');
  const results = search.querySelector('.results');
  const status = search.querySelector('[role=status]');
  const normalize = text => text.normalize('NFKC').toLocaleLowerCase('ko');
  const index = (window.koHelpIndex || []).map(entry => ({
    ...entry, text: normalize(entry.title + ' ' + entry.text),
  }));
  search.hidden = false;
  input.addEventListener('input', () => {
    const terms = normalize(input.value).trim().split(/\s+/).filter(Boolean);
    results.replaceChildren();
    status.textContent = '';
    if (!terms.length) return;
    const matches = index.filter(entry => terms.every(term => entry.text.includes(term)));
    status.textContent = matches.length ? `검색 결과 ${matches.length}개` : '검색 결과가 없습니다.';
    for (const entry of matches.slice(0, 12)) {
      const item = document.createElement('li');
      const link = document.createElement('a');
      const page = document.createElement('small');
      link.href = entry.href;
      link.textContent = entry.title;
      page.textContent = entry.page;
      item.append(link, page);
      results.append(item);
    }
  });
  input.addEventListener('keydown', event => {
    if (event.key === 'Enter') results.querySelector('a')?.focus();
    if (event.key === 'Escape') { input.value = ''; input.dispatchEvent(new Event('input')); }
  });
  const dialog = document.querySelector('dialog');
  const enlarged = dialog.querySelector('img');
  const caption = dialog.querySelector('p');
  const pixelSize = dialog.querySelector('.pixel-size');
  const scroll = dialog.querySelector('.image-scroll');
  const sizeImage = () => { enlarged.style.width = dialog.hasAttribute('data-native') && enlarged.naturalWidth ? (enlarged.naturalWidth / devicePixelRatio) + 'px' : ''; };
  enlarged.addEventListener('load', sizeImage);
  const showNative = enabled => {
    dialog.toggleAttribute('data-native', enabled);
    pixelSize.setAttribute('aria-pressed', String(enabled));
    pixelSize.textContent = enabled ? '화면에 맞추기' : '원본 크기';
    sizeImage();
    scroll.scrollTo(0, 0);
  };
  pixelSize.addEventListener('click', () => showNative(!dialog.hasAttribute('data-native')));
  document.querySelectorAll('figure a').forEach(link => link.addEventListener('click', event => {
    if (!dialog.showModal || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    enlarged.src = link.href;
    enlarged.alt = link.querySelector('img').alt;
    caption.textContent = link.closest('figure').querySelector('figcaption').textContent;
    showNative(false);
    dialog.showModal();
    dialog.querySelector('.image-close').focus();
  }));
  dialog.querySelector('.image-close').addEventListener('click', () => dialog.close());
  dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
  const chapters = [...document.querySelectorAll('.chapter')];
  const links = [...document.querySelectorAll('.section-links a')];
  const updateSection = () => {
    const current = chapters.filter(section => section.getBoundingClientRect().top < 160).at(-1) || chapters[0];
    for (const link of links) {
      if (link.hash === '#' + current?.id) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    }
  };
  addEventListener('scroll', updateSection, { passive: true });
  addEventListener('hashchange', updateSection);
  updateSection();
})();
