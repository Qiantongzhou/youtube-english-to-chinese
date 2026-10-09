using VideoCN;

int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception($"Assertion {passed + 1} failed"); passed++; }
void Reject(string text) {
    try { CookieConverter.Convert(text); throw new Exception("Invalid input accepted"); }
    catch (FormatException ex) { Check(!ex.Message.Contains("FAKE_SECRET")); }
}
var tsv = "Name\tValue\tDomain\tPath\tExpires / Max-Age\tSize\tHttpOnly\tSecure\tSameSite\n" +
          "LOGIN_INFO\tFAKE_SECRET==\t.youtube.com\t/\t2027-10-04T08:05:02.245Z\t20\t✓\t✓\tNone\n" +
          "EMPTY\t\twww.youtube.com\t/\tSession\t0\t\t\tLax";
var converted = CookieConverter.Convert(tsv);
Check(converted.Count == 2);
Check(converted.Text.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1822637102\tLOGIN_INFO\tFAKE_SECRET==\n"));
Check(converted.Text.Contains("www.youtube.com\tFALSE\t/\tFALSE\t0\tEMPTY\t\n"));
var md = "| Name | Value | Domain | Path | Expires / Max-Age | Size | HttpOnly | Secure |\n" +
         "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
         "| LOGIN\\_INFO | a\\|b\\:c\\_d | .youtube.com | / | 会话 | 10 | ✔ | true |";
Check(CookieConverter.Convert(md).Text.Contains("\tLOGIN_INFO\ta|b:c_d\n"));
Check(CookieConverter.Convert("Value\tName\tPath\tDomain\tExpires\nA\tSID\t/\t.youtube.com\t0").Text.Contains("\t0\tSID\tA\n"));
Check(CookieConverter.Convert("名称\t值\t域\t路径\t过期时间\nSID\tA\t.youtube.com\t/\t会话").Count == 1);
Check(CookieConverter.Convert("SID\t a\\_b|c \t.youtube.com\t/\t0").Text.EndsWith("\t a\\_b|c \n"));
Reject("");
Reject("| Name | Value | Domain | Path | Expires |\n|---|---|---|---|---|");
Reject("FAKE_SECRET");
Reject("SID\tFAKE_SECRET\t.youtube.com\t/\tbad-date");
Reject("SID\tFAKE_SECRET\thttps://youtube.com\t/\t0");
Reject("SID\tFAKE_SECRET\t.youtube.com\t/\t0\t10\tunknown\t✓");
Reject(tsv + "\nSID\tFAKE_SECRET\t.youtube.com\t/\t0");
var rich = "LOGIN\\_INFO\tFAKE\\_SECRET:a||b&x=1\t.youtube.com\t/\t2027-10-04T08:05:02.245Z\t40\t✓\t✓\tNone\t\t\tMedium&#x9;\n" +
           "NID\tFAKE\\_SECRET\t.google.cn\t/\tSession\t20\t✓\t&#x9;\nMedium&#x9;\n" +
           "VISITOR\\_INFO\tFAKE%3D%3D&amp;x\t.youtube.com\t/\t会话\t20\t✓\t✓\tNone\t[https://youtube.com](https://youtube.com)\t\tMedium&#x9;";
var richResult = CookieConverter.Convert(rich);
Check(richResult.Count == 3);
Check(richResult.Text.Contains("\tLOGIN_INFO\tFAKE_SECRET:a||b&x=1\n"));
Check(richResult.Text.Contains("#HttpOnly_.google.cn\tTRUE\t/\tFALSE\t0\tNID\tFAKE_SECRET\n"));
Check(richResult.Text.Contains("\tVISITOR_INFO\tFAKE%3D%3D&amp;x\n"));
Reject("Medium&#x9;");
Reject(rich + "\nFAKE_SECRET&#x9;");
Reject("NID\tFAKE_SECRET\t.google.cn\t/\t0\t20&#x9;\nMedium&#x9;");
Console.WriteLine($"Cookie converter: {passed} assertions passed.");
