// The local calendar day as yyyy-MM-dd: the key the Journal's addresses use, and the one the
// time-lapse files its frames under.
(function () {
  'use strict';

  const pad = (n) => String(n).padStart(2, '0');

  window.powatchLocalDay = (date) => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
})();
