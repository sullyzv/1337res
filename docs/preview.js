const profileButtons = document.querySelectorAll('.preview-profiles button');
const resolutionLabel = document.getElementById('mode-resolution');
const refreshLabel = document.getElementById('mode-refresh');
const displayFrame = document.getElementById('mode-frame');

for (const button of profileButtons) {
  button.addEventListener('click', () => {
    for (const profile of profileButtons) {
      profile.setAttribute('aria-pressed', String(profile === button));
    }

    resolutionLabel.textContent = button.dataset.resolution;
    refreshLabel.textContent = button.dataset.refresh;
    displayFrame.dataset.ratio = button.dataset.ratio;
  });
}
