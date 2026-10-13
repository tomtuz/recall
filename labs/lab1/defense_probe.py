"""Defense rehearsal: the "break it" requests a lecturer may try against a running API.

Each case sends one malformed or edge-case request and expects one exact status code
(never a 5xx). The probe works on a course/topic/card it creates and deletes itself.
Exit 1 if any request returns a different code. Requires the Development seed users.
Usage: python labs/lab1/defense_probe.py  (API must be running on http://localhost:15080)
"""
import base64
import json
import sys
import urllib.error
import urllib.request
import uuid

BASE = "http://localhost:15080/api/v1"


def send(method, path, body=None, raw=None, token=None, ctype="application/json"):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if ctype:
        req.add_header("Content-Type", ctype)
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def login(email, password):
    s, b = send("POST", "/auth/login", {"email": email, "password": password})
    if s != 200:
        sys.exit(f"login {email} failed ({s}): is the API running with the Development seed?")
    return json.loads(b)["accessToken"]


def create(path, body, token):
    s, b = send("POST", path, body, token=token)
    if s != 201:
        sys.exit(f"setup POST {path} failed ({s}): {b[:200]}")
    return json.loads(b)["id"]


def main():
    tok = login("author@recall.local", "Author123!")
    other = login("learner@recall.local", "Learner123!")
    course = create("/courses", {"code": f"PRB-{uuid.uuid4().hex[:8]}", "title": "Probe course",
                                 "visibility": "Public"}, tok)
    topic = create(f"/courses/{course}/topics", {"title": "Probe topic"}, tok)
    card = create(f"/topics/{topic}/cards", {"question": "Q", "answer": "A"}, tok)

    # Payload edited to role=Administrator, original signature kept: must be rejected.
    h, payload, sig = tok.split(".")
    claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    claims["role"] = "Administrator"
    forged = ".".join([h, base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip("="), sig])
    cases = [
        # Routing and ids
        ("unknown id", "GET", "/courses/999999", {}, 404),
        ("int overflow id", "GET", "/courses/99999999999", {}, 404),
        ("negative id", "GET", "/courses/-1", {}, 404),
        ("non-numeric id", "GET", "/courses/abc", {}, 404),
        ("unknown route", "GET", "/nope", {}, 404),
        ("wrong method PUT", "PUT", f"/courses/{course}", {"body": {}}, 405),
        # Query parameters: out-of-range paging is clamped; unknown filter/sort values are rejected
        ("page=0 clamped", "GET", "/courses?page=0", {}, 200),
        ("page=-5 clamped", "GET", "/courses?page=-5", {}, 200),
        ("pageSize=100000 clamped", "GET", "/courses?pageSize=100000", {}, 200),
        ("pageSize not a number", "GET", "/courses?pageSize=abc", {}, 400),
        ("unknown sort field", "GET", "/courses?sort=hacker", {}, 400),
        ("unknown visibility filter", "GET", "/courses?visibility=secret", {}, 400),
        ("unknown card state filter", "GET", f"/topics/{topic}/cards?state=bogus", {}, 400),
        ("numeric card state filter", "GET", f"/topics/{topic}/cards?state=2", {}, 400),
        ("SQL-ish search is plain text", "GET", "/courses?search=%27%3B%20DROP%20TABLE%20courses%3B--", {}, 200),
        # Authentication
        ("POST without token", "POST", "/courses", {"body": {"code": "X", "title": "X"}}, 401),
        ("forged role claim", "PATCH", f"/courses/{course}", {"body": {"title": "x"}, "token": forged}, 401),
        ("garbage token", "POST", "/courses", {"body": {"code": "X", "title": "X"}, "token": "abc.def.ghi"}, 401),
        ("dashboard without token", "GET", "/me/dashboard", {}, 401),
        ("refresh without cookie", "POST", "/auth/refresh", {"body": {}}, 401),
        ("login wrong password", "POST", "/auth/login", {"body": {"email": "author@recall.local", "password": "nope"}}, 401),
        # Request bodies
        ("malformed JSON", "POST", "/courses", {"raw": b"{\"code\": ", "token": tok}, 400),
        ("empty body", "POST", "/courses", {"raw": b"", "token": tok}, 400),
        ("JSON array body", "POST", "/courses", {"raw": b"[]", "token": tok}, 400),
        ("wrong field types", "POST", "/courses", {"body": {"code": 5, "title": ["x"]}, "token": tok}, 400),
        ("missing required fields", "POST", "/courses", {"body": {}, "token": tok}, 400),
        ("invalid enum value", "POST", "/courses", {"body": {"code": "Z1", "title": "T", "visibility": "Secret"}, "token": tok}, 400),
        ("title over the length limit", "POST", "/courses", {"body": {"code": "Z2", "title": "x" * 5000}, "token": tok}, 400),
        ("text/plain body", "POST", "/courses", {"raw": b"hello", "token": tok, "ctype": "text/plain"}, 415),
        ("review rating not allowed", "POST", f"/cards/{card}/review", {"body": {"rating": "perfect"}, "token": tok}, 400),
        ("import body not a card list", "POST", f"/topics/{topic}/cards/import", {"body": {"x": 1}, "token": tok}, 400),
        ("import over 200 cards", "POST", f"/topics/{topic}/cards/import",
         {"body": {"cards": [{"question": "Q", "answer": "A"}] * 201}, "token": tok}, 400),
        ("register duplicate email", "POST", "/auth/register", {"body": {"email": "author@recall.local", "password": "Author123!", "displayName": "dup"}}, 409),
        ("register invalid email", "POST", "/auth/register", {"body": {"email": "not-an-email", "password": "x"}}, 400),
        # Ownership: another user's public course is readable but not writable
        ("foreign PATCH", "PATCH", f"/courses/{course}", {"body": {"title": "hijack"}, "token": other}, 403),
        ("foreign DELETE", "DELETE", f"/courses/{course}", {"token": other}, 403),
        ("foreign card review", "POST", f"/cards/{card}/review", {"body": {"rating": "good"}, "token": other}, 403),
        # Hierarchy
        ("topic under unknown course", "POST", "/courses/999999/topics", {"body": {"title": "t"}, "token": tok}, 404),
        ("cards under unknown topic", "GET", "/topics/999999/cards", {}, 404),
        ("topic not in this course", "GET", f"/courses/{course}/topics/999999/cards", {}, 404),
        ("delete course that has topics", "DELETE", f"/courses/{course}", {"token": tok}, 409),
    ]
    bad = 0
    try:
        for name, m, p, kw, expected in cases:
            s, _ = send(m, p, body=kw.get("body"), raw=kw.get("raw"), token=kw.get("token"),
                        ctype=kw.get("ctype", "application/json"))
            bad += s != expected
            print(f"{'OK ' if s == expected else 'BAD'} {s} {m} {p[:60]}  ({name}; expected {expected})")
    finally:
        # Children first: the API refuses to delete a parent that still has children (409).
        for path in (f"/cards/{card}", f"/topics/{topic}", f"/courses/{course}"):
            send("DELETE", path, token=tok)
    print(f"\n{len(cases) - bad}/{len(cases)} as expected, {bad} unexpected")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
