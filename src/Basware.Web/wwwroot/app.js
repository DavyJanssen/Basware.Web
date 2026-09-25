window.basware = {
  download: async (name, type, stream) => {
    const blob = new Blob([await stream.arrayBuffer()], { type });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a'); a.href = url; a.download = name;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 30000);
  },
  print: async id => {
    const frame = document.getElementById(id);
    if (!frame?.contentWindow) return;
    await frame.contentDocument.fonts.ready;
    frame.contentWindow.focus(); frame.contentWindow.print();
  }
};
