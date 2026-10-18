import { defineConfig, type DefaultTheme } from 'vitepress';

const repository = 'https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions';

function sidebar(prefix: string, ru: boolean): DefaultTheme.SidebarItem[] {
  const page = (path: string) => `${prefix}/${path}`;
  return [
    {
      text: ru ? 'Начало работы' : 'Start here',
      items: [
        { text: ru ? 'Обзор' : 'Overview', link: page('') },
        { text: ru ? 'Установка и первый пример' : 'Installation and first example', link: page('getting-started') },
      ],
    },
    {
      text: ru ? 'Руководства' : 'Guides',
      items: [
        { text: ru ? 'Блокировки' : 'Locking', link: page('query-integration') },
        { text: ru ? 'Данные и транзакции' : 'Data and transactions', link: page('internal-mechanisms') },
        { text: ru ? 'Настройка и диагностика' : 'Configuration and diagnostics', link: page('capabilities') },
      ],
    },
    {
      text: ru ? 'Справочник' : 'Reference',
      items: [
        { text: ru ? 'Совместимость' : 'Compatibility', link: page('compatibility') },
        { text: ru ? 'Архитектура' : 'Architecture', link: page('architecture') },
      ],
    },
  ];
}

export default defineConfig({
  title: 'PostgreSQL Extensions',
  description: 'Typed PostgreSQL operations for Entity Framework Core and Npgsql.',
  base: '/EntityFrameworkCore.PostgreSQL.Extensions/',
  cleanUrls: false,
  lastUpdated: false,
  locales: {
    root: {
      label: 'English', lang: 'en',
      themeConfig: {
        nav: [{ text: 'Documentation', link: '/getting-started' }, { text: 'Contributing', link: `${repository}/blob/develop/CONTRIBUTING.md` }],
        sidebar: sidebar('', false),
      },
    },
    ru: {
      label: 'Русский', lang: 'ru',
      description: 'Типизированные операции PostgreSQL для Entity Framework Core и Npgsql.',
      themeConfig: {
        nav: [{ text: 'Документация', link: '/ru/getting-started' }, { text: 'Участие в разработке', link: `${repository}/blob/develop/CONTRIBUTING.md` }],
        sidebar: sidebar('/ru', true),
        outline: { label: 'На этой странице', level: [2, 3] },
        docFooter: { prev: 'Предыдущая страница', next: 'Следующая страница' },
        returnToTopLabel: 'Наверх', sidebarMenuLabel: 'Меню',
        darkModeSwitchLabel: 'Тема', lightModeSwitchTitle: 'Светлая тема', darkModeSwitchTitle: 'Тёмная тема',
        langMenuLabel: 'Язык', skipToContentLabel: 'Перейти к содержимому',
      },
    },
  },
  themeConfig: {
    siteTitle: 'PostgreSQL Extensions',
    outline: { level: [2, 3] },
    socialLinks: [{ icon: 'github', link: repository }],
    search: {
      provider: 'local',
      options: {
        locales: {
          ru: {
            translations: {
              button: { buttonText: 'Поиск', buttonAriaLabel: 'Поиск по документации' },
              modal: {
                displayDetails: 'Показать подробности', resetButtonTitle: 'Очистить', backButtonTitle: 'Закрыть поиск',
                noResultsText: 'Ничего не найдено',
                footer: { selectText: 'выбрать', navigateText: 'перейти', closeText: 'закрыть' },
              },
            },
          },
        },
      },
    },
  },
});
