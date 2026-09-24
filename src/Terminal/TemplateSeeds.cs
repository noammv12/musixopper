namespace Palon.Terminal;

/// <summary>The five templates the user sends every day, verbatim from the
/// approved design (the rep name "נועם" is part of the text on purpose).</summary>
static class TemplateSeeds
{
    public static List<MessageTemplate> Create() => new()
    {
        new MessageTemplate { Id = "welcome", Title = "ברוך הבא", Tag = "אחרי שהלקוח הפקיד", Region = "פרו", Mode = TemplateMode.Replace, Head = "ברוך הבא ",
            Text = "ברוך הבא!\nלנוחיותך מצרף לך את מספרי הטלפון הבאים שיוכלו לשמש אותך מכאן והלאה (בנוסף לנציג האישי שיוצמד אליך)\nדילינג פרו - 0732580001\nשירות לקוחות צ׳אט - ווטסאפ פרו\nhttps://api.whatsapp.com/send/?phone=972538281878\n\nמייל לשירות הלקוחות - support@colmexpro.com\n\nסרטוני הדרכה על המערכות:\n\nסרטוני הסבר על התוכנה 👇🏻\n\nhttps://www.youtube.com/watch?v=DzoUh-rfPYE&list=PLZyt2aPobnAxdb2JRQtLjf0ST6X_ZB0IT\nסרטוני הסבר על האפליקציה 👇🏻\nhttps://www.youtube.com/playlist?list=PLZyt2aPobnAzQKtJ9uSOqP3BwD3XjhOqp\nסרטוני הדרכה על טריידינג וויו:\nhttps://www.youtube.com/watch?v=W27w-eDXuFU" },
        new MessageTemplate { Id = "open-pro", Title = "פתיחה · קולמקס פרו", Tag = "אחרי שיחה על פרו", Region = "פרו", Mode = TemplateMode.Prepend, Head = "",
            Text = "שמחתי לשוחח איתך, נועם מקולמקס פרו\nתעדכן אותי אם יש לך שאלות על הפלטפורמה או דברים לא ברורים, אני כאן\nאשמח לעזור לך לאורך הדרך 🙏🏻\n\nקולמקס פרו - חשבון מסחר – מדוע אצלנו?\n\n• נציג אישי צמוד אליך - שיעשה איתך הדרכה על המערכת וזמין עבורך לכל שאלה\n• חשבון דמו ללא הגבלה\n• גישה לשורטים במערכת.\n• כולל פני סטוקס ו- OTC.\n• בקולמקס פרו אין מגבלה בפעולות.\n• חדר שירות ותמיכה טכנית כל יום עד 23:00.\n• אפשרות לביצוע פעולות ישירות מתוך טריידינג ויו!\n\nהנציג/ה שלך בקולמקס פרו: נועם\n\nלאתר שלנו: https://www.colmexpro.com/" },
        new MessageTemplate { Id = "open-israel", Title = "פתיחה · קולמקס ישראל", Tag = "אחרי שיחה על ישראל", Region = "ישראל", Mode = TemplateMode.Replace, Head = "היי ",
            Text = "היי !\nשמחתי לשוחח איתך, נועם מקולמקס ישראל\nתעדכן אותי אם יש לך שאלות על הפלטפורמה או דברים לא ברורים, אני כאן\nאשמח לעזור לך לאורך הדרך 🙏🏻\n\nקולמקס ישראל - חשבון מסחר – מדוע אצלנו?\n\n1. פיקוח הרשות לניירות ערך בישראל.\n2. חדר עסקאות ותמיכה 24 שעות ביממה, בכל ימי המסחר (6 ימים בשבוע) כולל ראשון מסחר ישראלי.\n3. נציג קשרי לקוחות אישי להדרכות מערכת.\n4. מגוון אפיקי השקעה במערכת מסחר אחת- מניות, מדדים, מט\"ח, סחורות.\n5. אפליקציית מסחר חדשה ומתקדמת- colmex plus.\n6. משיכת כספים עד יום עסקים אחד.\n7. פקודות מתקדמות ומסחר על הגרף.\n\nעלויות:\n\n1. פטור מעמלת הפקדה.\n2. פטור מעמלת משיכה שקלית.\n3. פטור מעלות שימוש במערכת.\n4. סוואפים מהנמוכים בשוק.\n5. מרווחים מהנמוכים בשוק.\n6. לפירוט המסלולים- https://www.colmex.co.il/costs/\n(הסכום הנדרש לצורך פתיחת החשבון: 300 דולר, המשמשים אותך למסחר עצמו).\n\nהנציג/ה שלך בקולמקס : נועם" },
        new MessageTemplate { Id = "questionnaire", Title = "מענה על שאלון", Tag = "כשמדברים על השאלון", Region = "פרו", Mode = TemplateMode.Prepend, Head = "",
            Text = "מענה על שאלון - https://my.colmexpro.com/signup?lang=he" },
        new MessageTemplate { Id = "deposit-pro", Title = "הפקדת כספים · פרו", Tag = "כשמדברים על הפקדה", Region = "פרו", Mode = TemplateMode.Prepend, Head = "",
            Text = "הפקדת כספים-קולמקס פרו:\n\nלפקודת קולמקס פרו,\nבנק לאומי, סניף 855\nמספר חשבון: 64570006\n\nנא לשלוח צילום מסך של ההפקדה אליי לווטסאפ." },
    };
}
