-- Datos de desarrollo. Se aplica IGUAL en MAIN y en LOCAL (los ids deben coincidir entre bases).
-- Idempotente: se puede correr varias veces.
-- Si tu esquema tiene columnas NOT NULL adicionales en users/customers/customer_credit, el script
-- fallará con un mensaje claro: agrega la columna aquí con un valor razonable.

-- 1) Usuario de pruebas: dev / dev123  (el POS compara SHA-256 en hex, LoginWindow.ComputeSha256)
INSERT INTO users (username, full_name, role_code, password_hash, password_algo, is_active)
SELECT 'dev', 'Dev Tester',
       (SELECT role_code FROM users WHERE is_active = 1 AND role_code IS NOT NULL LIMIT 1),
       SHA2('dev123', 256), 'SHA256', 1
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = 'dev');

UPDATE users
   SET password_hash = SHA2('dev123', 256), password_algo = 'SHA256', is_active = 1
 WHERE username = 'dev';

-- 2) Settings de desarrollo (upsert por (site_id, key))
--    scale.com = COM99  -> el puerto no existe -> el POS arranca el simulador de báscula
--    sync.push_timer_minutes = 1 -> un ciclo de sync por minuto
--    tickets.printer_name = TS-NULL -> impresora que descarta (docs/entorno-pruebas.md §3.7)
INSERT INTO settings (site_id, `key`, value, is_active) VALUES
  (1, 'scale.com',               'COM99',   1),
  (1, 'sync.push_timer_minutes', '1',       1),
  (1, 'tickets.printer_name',    'TS-NULL', 1)
ON DUPLICATE KEY UPDATE value = VALUES(value), is_active = 1;

-- 3) Clientes de prueba con crédito (ids fijos para que coincidan en MAIN y LOCAL)
--    990001 = POSTPAID con saldo disponible  (caso TRAMUC / RAPID)
--    990002 = PREPAID  con $500              (caso Trisa)
INSERT INTO customers (id_customer, account_number, account_name, account_address,
                       account_country, account_state, has_credit, is_active)
VALUES (990001, 'TS-POST', 'TS TEST POSTPAID', 'Test', 'US', 'TX', 1, 1),
       (990002, 'TS-PRE',  'TS TEST PREPAID',  'Test', 'US', 'TX', 1, 1)
ON DUPLICATE KEY UPDATE has_credit = 1, is_active = 1;

INSERT INTO customer_credit (credit_id, customer_id, credit_type, credit_limit, current_balance,
                             available_credit, is_suspended, payment_terms_days)
VALUES (990001, 990001, 'POSTPAID', 1000.00, 0.00, 1000.00, 0, 30),
       (990002, 990002, 'PREPAID',     0.00, 0.00,  500.00, 0,  0)
ON DUPLICATE KEY UPDATE credit_type = VALUES(credit_type), credit_limit = VALUES(credit_limit),
                        current_balance = VALUES(current_balance),
                        available_credit = VALUES(available_credit), is_suspended = 0;

SELECT 'seed-dev OK' AS resultado;
