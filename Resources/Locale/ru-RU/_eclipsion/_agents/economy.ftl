stock-market-price-up = [color=lime]РОСТ[/color]
stock-market-price-down = [color=red]ПАДЕНИЕ[/color]
stock-market-balance = Баланс: {$balance}
stock-market-qty = Кол-во:
stock-market-tab-market = Рынок
stock-market-total-value = Общая стоимость: {$value} кр
stock-market-no-trades = В эту смену сделок не было
stock-market-history-buy = Куплено
stock-market-history-sell = Продано
stock-market-toast-bought = Куплено {$amount} × {$company} за {$total} кр
stock-market-toast-sold = Продано {$amount} × {$company} за {$total} кр
stock-market-tab-news = Новости
stock-market-delisted = {$shares} — сняты с торгов в эту смену
stock-market-no-news = На рынке затишье
stock-news-casualties = Несёт потери
stock-news-merchant-war = Военный спрос растёт
stock-news-station-lost = {$station} потеряна
stock-news-rebuild = Контракты на восстановление {$station}
stock-news-victory = Победа в войне
stock-news-contract = Контракт выполнен
stock-news-treasury-up = Казна растёт
stock-news-treasury-down = Казна тает
stock-news-territory-income = Владеет {$region}
stock-news-admin = Необъяснимое движение рынка
stock-war-liquidation = {$company} вступила в войну с вашей фракцией и закрыла вашу позицию. Ваши акции ({$shares} шт.) проданы по минимальной цене {$price} кр, и {$total} кр переведено на ваш счёт.
stock-market-price-now = {$price} кр
stock-market-price-now-tooltip = Сколько стоит одна акция прямо сейчас — при покупке и при продаже.
stock-market-trend-row = {$window} {$change}
stock-market-trend-row-unknown = {$window} —
stock-market-trend-tooltip = Тренд: как изменилась цена акции за последние {$window}. Именно эта цифра показывает, куда цена движется сейчас.
stock-market-vs-open-row = откр. {$change}
stock-market-vs-open-tooltip = Положение цены относительно {$open} кр, по которым компания впервые открылась, за все смены, когда она торговалась. Это НЕ тренд: акция может весь день резко расти и всё равно быть ниже цены открытия.
stock-market-pending-row = {$change} зафиксировано, осталось {$time}
stock-market-pending-tooltip = Движение, которое события раунда уже оплатили и которое ещё выплачивается тик за тиком. Это не прогноз, и ничто его не отменит — оно обязательно наступит.
stock-market-chart-tooltip = Цена за последние {$minutes} мин. Горизонтальная линия — {$open} кр, по которым открылась компания, а линия графика окрашена по значению тренда под ним.
stock-market-kind-faction-tooltip = Компания фракции. В ней нет никакой случайности: она движется, только когда что-то происходит с этой фракцией в раунде. Каждое движение отражено строкой во вкладке «Новости».
stock-market-kind-neutral-tooltip = Нейтральная компания. Она понемногу колеблется сама по себе и тянется обратно к цене открытия, а ещё зарабатывает на хаосе, который устраивают фракции. Здесь можно укрыться, когда тонут все фракции.
stock-market-owned-worth = Во владении: {$shares} ({$value} кр)
stock-market-buy-tooltip = Купить {$amount} акц. за {$total} кр.
stock-market-sell-tooltip = Продать {$amount} акц. за {$total} кр.
stock-market-delisted-tooltip = Эта фракция не участвует в этой смене, поэтому её компания не торгуется. Акции остаются у вас и переносятся дальше — просто продать их нельзя, пока она не вернётся.
stock-market-duration-seconds = {$value} с
stock-market-duration-minutes = {$value} м
stock-market-news-header = Почему изменились цены:
stock-market-news-detail = {$reason} · {$ago} назад
stock-market-tab-guide = Справка
stock-guide-basics-title = Как это работает
stock-guide-basics-body =
    Цены обновляются каждые 15 секунд.

    Компании на бирже делят между собой один фиксированный объём рыночной стоимости. Компания может расти, только забирая стоимость у других, поэтому весь рынок никогда не растёт одновременно. Если что-то дорожает, за это платит что-то другое.
stock-guide-row-title = Как читать строку
stock-guide-row-body =
    327 кр — сколько стоит одна акция прямо сейчас.

    5m ▲2.1% — тренд. Как изменилась цена за последние пять минут. Именно по этой цифре стоит торговать.

    откр. ▼6.5% — положение цены относительно цены, по которой компания впервые открылась, за всё время. Между сменами не сбрасывается. Это не тренд: акция, резко растущая всю смену, всё равно может показывать откр. ▼6.5%.

    «зафиксировано» — движение, которое события раунда уже оплатили, но которое ещё не до конца наступило. Это не догадка. Оно наступит, и строка показывает, сколько ему ещё осталось.
stock-guide-faction-title = Компании фракций
stock-guide-faction-body =
    SHI, TFCF, Olywier Charter Holdings и Potato Trade Corp не содержат никакой случайности. Сами по себе они никогда не двигаются.

    Они меняются только тогда, когда что-то происходит с их фракцией: гибнут её люди, теряется одна из её станций, казна растёт или тает, выполнен контракт, выиграна война. Каждое такое событие пишет строку во вкладке «Новости», так что всегда можно точно узнать, почему изменилась цена.

    Фракция, не участвующая в этой смене, снимается с торгов. Её цена замораживается, ею нельзя торговать, и она не восстанавливается.
stock-guide-neutral-title = Нейтральные компании
stock-guide-neutral-body =
    Free Merchant Nobles и Taypan Civil Construction колеблются сами по себе и со временем тянутся обратно к цене открытия.

    А ещё они кормятся войной: Торговцы зарабатывают на потерях, Тайпан — на станциях, которые нужно восстанавливать. В них прячутся, когда все фракции тонут разом.
stock-guide-asymmetry-title = Рост медленный, падение быстрое
stock-guide-asymmetry-body =
    Каждый толчок вверх срабатывает на 70% своей силы, а каждый толчок вниз — на 120%. Одно и то же событие опускает цену сильнее, чем поднимает.

    Удерживать выигрышную позицию — это работа, а не прогулка.
stock-guide-persistence-title = Цены сохраняются
stock-guide-persistence-body =
    Рынок не сбрасывается вместе с раундом. Фракция, проигрывавшая три смены, открывает четвёртую дёшево.

    Компания, торговавшаяся в эту смену, при смене раунда сокращает разрыв с ценой открытия на 20%. Снятая с торгов — нет: она возвращается ровно по той цене, до которой рухнула. Восстанавливаются игрой.
stock-guide-liquidation-title = Военная ликвидация
stock-guide-liquidation-body =
    Если у вас есть акции компании, чья фракция вступает в войну с вашей, ваша позиция закрывается принудительно по минимальной цене — 25% от цены открытия компании.

    Это намного ниже рыночной стоимости акций. Ликвидация — это убыток, а не выход.
cmd-stockmarket-desc = Просмотр и управление фондовым рынком сектора.
cmd-stockmarket-help = Использование: {$command} <list | reset | freeze | clearportfolios | shock <company> <percent> [ticks]>
cmd-stockmarket-reset = Рынок сброшен к долям открытия.
cmd-stockmarket-frozen = Рынок заморожен. Цены не изменятся до разморозки.
cmd-stockmarket-unfrozen = Рынок снова работает.
cmd-stockmarket-portfolios-cleared = Все сохранённые активы игроков удалены.
cmd-stockmarket-unknown-company = Нет такой компании: {$id}
cmd-stockmarket-hint-subcommand = <подкоманда>
cmd-stockmarket-hint-company = <id компании>
