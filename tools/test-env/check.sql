-- Invariantes de ventas. Se ejecuta con: .\check.ps1 [main|local|both]
-- Cualquier fila en las secciones 1-4 es una violación.

SELECT '1) Ventas COMPLETED sin ninguna fila en payments (bug B)' AS chequeo;
SELECT s.sale_uid, t.ticket_number, s.customer_id, s.total, s.amount_paid, s.balance_due, s.created_at
FROM sales s
LEFT JOIN payments p ON p.sale_uid = s.sale_uid
LEFT JOIN tickets  t ON t.sale_uid = s.sale_uid
WHERE s.sale_status_id = 2 AND p.payment_uid IS NULL
ORDER BY s.created_at DESC LIMIT 20;

SELECT '2) amount_paid distinto de la suma de pagos (bug B/V)' AS chequeo;
SELECT s.sale_uid, s.total, s.amount_paid, COALESCE(SUM(p.amount), 0) AS suma_pagos, s.created_at
FROM sales s
LEFT JOIN payments p ON p.sale_uid = s.sale_uid
WHERE s.sale_status_id = 2
GROUP BY s.sale_uid, s.total, s.amount_paid, s.created_at
HAVING s.amount_paid <> COALESCE(SUM(p.amount), 0)
ORDER BY s.created_at DESC LIMIT 20;

SELECT '3) Pago Business (method_id=3) en venta sin customer_id (bug V / huérfanos)' AS chequeo;
SELECT s.sale_uid, p.payment_uid, p.payment_status_id, p.amount, s.created_at
FROM payments p
JOIN sales s ON s.sale_uid = p.sale_uid
WHERE p.method_id = 3 AND s.customer_id IS NULL
ORDER BY s.created_at DESC LIMIT 20;

SELECT '4) customer_id vs sale_driver_info.account_number (bug A)' AS chequeo;
SELECT s.sale_uid, s.customer_id, c.account_number AS cuenta_segun_customer_id,
       d.account_number AS cuenta_en_driver_info, d.account_name, s.created_at
FROM sales s
LEFT JOIN customers c        ON c.id_customer = s.customer_id
LEFT JOIN sale_driver_info d ON d.sale_uid    = s.sale_uid
WHERE s.sale_status_id = 2
  AND NOT (c.account_number <=> NULLIF(d.account_number, ''))
ORDER BY s.created_at DESC LIMIT 20;

SELECT '5) Crédito de los clientes de prueba (comparar updated_at con la hora de la venta)' AS chequeo;
SELECT customer_id, credit_type, credit_limit, current_balance, available_credit, updated_at
FROM customer_credit WHERE customer_id IN (990001, 990002);

SELECT '6) Últimas 5 ventas' AS chequeo;
SELECT s.sale_uid, t.ticket_number, s.customer_id, s.total, s.amount_paid,
       (SELECT COUNT(*) FROM payments p WHERE p.sale_uid = s.sale_uid) AS n_pagos,
       (SELECT GROUP_CONCAT(CONCAT(p.method_id, ':', p.payment_status_id, ':', p.amount))
          FROM payments p WHERE p.sale_uid = s.sale_uid) AS pagos_metodo_estado_monto,
       s.created_at
FROM sales s LEFT JOIN tickets t ON t.sale_uid = s.sale_uid
ORDER BY s.created_at DESC LIMIT 5;
